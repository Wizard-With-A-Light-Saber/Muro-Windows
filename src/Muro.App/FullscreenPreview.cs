using System.Runtime.InteropServices;
using Muro.Platform;
using Muro.Playback;

namespace Muro.App;

/// A borderless full-screen window that plays a video in Muro own mpv, so a wallpaper is previewed
/// at its true size on the real display. Esc, a click or any key closes it. Always on the UI thread.
internal static class FullscreenPreview
{
    private const string ClassName = "MuroPreviewWindow";
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_EX_TOPMOST = 0x8;
    private const uint WM_CLOSE = 0x10, WM_ERASEBKGND = 0x14, WM_KEYDOWN = 0x100, WM_TIMER = 0x113, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204;
    private const int GWLP_WNDPROC = -4;

    private static readonly Native.WndProc Proc = WndProc;
    private static readonly Native.WndProc ChildProc = ChildWndProc;
    private static nint _window;
    private static nint _originalChildProc;
    private static MpvPlayer? _player;
    private static bool _registered;
    private static int _attempts;
    private static Task? _closing;

    [DllImport("gdi32")] private static extern nint GetStockObject(int index);

    public static void Show(string source)
    {
        Close();
        // A preview still shutting down owns the subclassed mpv window; wait for it to finish.
        if (_closing is { IsCompleted: false }) return;
        _originalChildProc = 0;
        Register();
        var bounds = TargetMonitor();
        _window = Native.CreateWindowEx(WS_EX_TOPMOST, ClassName, "Muro preview", WS_POPUP | WS_VISIBLE,
            (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, 0, 0, Native.GetModuleHandle(null), 0);
        if (_window == 0) return;
        Native.SetForegroundWindow(_window);
        try
        {
            _player = new MpvPlayer(_window, fill: false);
            _player.Load(source);
        }
        catch
        {
            Close();
            throw;
        }
        // mpv creates its own child window a moment later; take its input so a click or key closes the preview.
        _attempts = 0;
        Native.SetTimer(_window, 1, 100, 0);
    }

    public static void Close()
    {
        if (_window == 0) return;
        var window = _window;
        var player = _player;
        _window = 0;
        _player = null;
        // Off the screen at once. mpv then shuts down on its own threads and may send messages to this
        // window meanwhile, so the UI thread must stay free to answer them: blocking it on the shutdown
        // deadlocks. The window itself is destroyed afterwards, on the thread that created it.
        Native.ShowWindow(window, 0);
        var queue = MainWindow.Instance?.DispatcherQueue;
        // The original window procedure stays set until mpv is gone: its own window must still get
        // the destroy messages or its shutdown never finishes.
        _closing = Task.Run(() =>
        {
            try { player?.Dispose(); } catch { }
            if (queue is null || !queue.TryEnqueue(() => Native.DestroyWindow(window))) Native.PostMessage(window, WM_CLOSE, 0, 0);
        });
    }

    private static Muro.Core.Rect TargetMonitor()
    {
        var all = Monitors.All();
        var handle = MainWindow.Instance is { } main ? Native.MonitorFromWindow(main.Handle, Native.MONITOR_DEFAULTTONEAREST) : 0;
        var info = new Native.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (handle != 0 && Native.GetMonitorInfo(handle, ref info) && all.FirstOrDefault(m => m.DeviceName == info.szDevice) is { } match)
            return match.Bounds;
        return (all.FirstOrDefault(m => m.Primary) ?? all[0]).Bounds;
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null),
            hbrBackground = GetStockObject(4),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _registered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_KEYDOWN:
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
                Close();
                return 0;
            case WM_CLOSE:
                // Already closing means mpv is down and this is the final destroy.
                if (_window == hwnd) Close(); else Native.DestroyWindow(hwnd);
                return 0;
            case WM_TIMER when (int)wParam == 1:
                TryTakeChildInput(hwnd);
                return 0;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void TryTakeChildInput(nint hwnd)
    {
        nint child = 0;
        Native.EnumChildWindows(hwnd, (c, _) => { child = c; return false; }, 0);
        if (child == 0)
        {
            if (++_attempts > 30) Native.KillTimer(hwnd, 1);
            return;
        }
        Native.KillTimer(hwnd, 1);
        _originalChildProc = Native.SetWindowLongPtr(child, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(ChildProc));
    }

    /// Runs on mpv own window thread: forward closing input to the preview window and pass the rest on.
    private static nint ChildWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg is WM_KEYDOWN or WM_LBUTTONDOWN or WM_RBUTTONDOWN)
        {
            if (_window != 0) Native.PostMessage(_window, WM_CLOSE, 0, 0);
            return 0;
        }
        return _originalChildProc != 0 ? Native.CallWindowProc(_originalChildProc, hwnd, msg, wParam, lParam) : Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
