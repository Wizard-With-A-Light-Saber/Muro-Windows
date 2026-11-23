using Microsoft.Web.WebView2.Core;
using Muro.Core;
using Muro.Platform;

namespace Muro.Engine;

/// One screen's interactive wallpaper: the page, drawn by WebView2 in a child window over the still, below the desktop
/// icons. Everything WebView2 runs on one STA thread shared by every screen (see <see cref="StaThread"/>).
/// The page is locked in: it is served from its own folder under a virtual host name and nothing else, it cannot go
/// online, open windows, ask for permissions or see other files. Muro sends it three events on <c>window</c>:
/// muro:mouse {x, y, inside}, muro:click {x, y} and muro:audio {bands, bass, mid, treble, level}.
internal sealed class InteractiveLayer : IDisposable
{
    private const string HostName = "muro.local";
    private static StaThread? _thread;
    private static Task<CoreWebView2Environment>? _environment;
    private static StaThread Thread => _thread ??= new StaThread("Muro interactive");

    private InteractiveSurface? _surface;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private volatile bool _disposed, _ready;

    /// The page file this layer shows.
    public string Page { get; }

    public InteractiveLayer(DesktopHost.Target host, Rect bounds, string pagePath)
    {
        Page = pagePath;
        Thread.Post(() => _ = StartAsync(host, bounds));
    }

    private static Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "WebView2"));

    private async Task StartAsync(DesktopHost.Target host, Rect bounds)
    {
        try
        {
            _surface = new InteractiveSurface(host, bounds);
            var env = await Environment();
            if (_disposed) { Cleanup(); return; }
            _controller = await env.CreateCoreWebView2ControllerAsync(_surface.Handle);
            if (_disposed) { Cleanup(); return; }
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, _surface.Width, _surface.Height);
            _controller.DefaultBackgroundColor = System.Drawing.Color.Black;
            _controller.IsVisible = true;

            var web = _web = _controller.CoreWebView2;
            var settings = web.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;

            // Only this wallpaper's own folder is reachable, and only under one fixed name.
            web.SetVirtualHostNameToFolderMapping(HostName, Path.GetDirectoryName(Page)!, CoreWebView2HostResourceAccessKind.DenyCors);
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                if (uri.Host != HostName && uri.Scheme is not ("data" or "blob"))
                    e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            web.NavigationStarting += (_, e) =>
            {
                if (new Uri(e.Uri) is { } uri && uri.Host != HostName && uri.Scheme is not ("data" or "blob" or "about")) e.Cancel = true;
            };
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.ProcessFailed += (_, e) => Log.Info("interactive wallpaper page process: " + e.ProcessFailedKind);

            web.Navigate("https://" + HostName + "/" + Uri.EscapeDataString(Path.GetFileName(Page)));
            _ready = true;
            Log.Info("interactive wallpaper: " + Path.GetFileName(Path.GetDirectoryName(Page)));
        }
        catch (Exception ex)
        {
            // No WebView2 runtime, or the shell moved: the still keeps playing, which is a fine wallpaper on its own.
            Log.Info("interactive wallpaper could not start: " + ex.Message);
            Cleanup();
        }
    }

    /// Delivers one muro:* event to the page. Cheap to call often; dropped until the page is up.
    public void Dispatch(string name, string detailJson) =>
        Thread.Post(() =>
        {
            if (_ready && !_disposed)
                _web?.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('" + name + "',{detail:" + detailJson + "}))");
        });

    /// Runs a script in the page and returns its JSON result. For the engine's self-test.
    public Task<string> EvaluateAsync(string script)
    {
        var done = new TaskCompletionSource<string>();
        Thread.Post(async () =>
        {
            try { done.SetResult(_ready && _web is not null ? await _web.ExecuteScriptAsync(script) : "\"not ready\""); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    /// Hidden while the screen is frozen (a full screen app, battery, lock): WebView2 then stops drawing.
    public void SetVisible(bool visible) =>
        Thread.Post(() => { if (_controller is not null && !_disposed) _controller.IsVisible = visible; });

    private void Cleanup()
    {
        try { _controller?.Close(); } catch { }
        _controller = null; _web = null; _ready = false;
        _surface?.Dispose(); _surface = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Thread.Post(Cleanup);
    }
}
