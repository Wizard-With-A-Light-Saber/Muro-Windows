using System.Runtime.InteropServices;

namespace Muro.Platform;

public static class WindowTools
{
    /// Restores a minimised window and brings it to the front.
    public static void Raise(nint hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        Native.SetForegroundWindow(hwnd);
    }
}

/// A notification-area icon with a right-click menu, on a hidden window of its own. Create and use it
/// on the thread that runs <see cref="MessageLoop"/>. It puts the icon back when Explorer restarts.
public sealed class TrayIcon : IDisposable
{
    /// <param name="Children">Makes this a submenu; its own Click is then unused.</param>
    public sealed record Item(string Text, Action? Click = null, bool Checked = false, bool Enabled = true, IReadOnlyList<Item>? Children = null)
    {
        public static readonly Item Separator = new("-");
        public bool IsSeparator => ReferenceEquals(this, Separator);
    }

    private const string ClassName = "MuroEngineTray";
    private const uint CallbackMessage = Native.WM_APP + 1;

    private readonly Native.WndProc _proc;
    private readonly string _tooltip;
    private readonly uint _taskbarCreated;
    private const nuint RetryTimer = 1;

    private nint _hwnd, _icon;
    private bool _wanted, _added;
    private int _retries;

    /// Asked each time the menu opens, so it always reflects the current state.
    public Func<IReadOnlyList<Item>>? Menu { get; set; }
    /// The icon was clicked with the left button.
    public event Action? Clicked;
    public event Action<Exception>? Error;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;
        _proc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        // Top-level and never shown: message-only windows do not receive the TaskbarCreated broadcast.
        _hwnd = Native.CreateWindowEx(0, ClassName, "Muro tray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException("tray window failed");
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _icon = LoadOwnIcon();
    }

    /// The icon embedded in this executable (ApplicationIcon), or the stock application icon.
    private static nint LoadOwnIcon()
    {
        var small = new nint[1];
        if (Environment.ProcessPath is { } exe && Native.ExtractIconEx(exe, 0, null, small, 1) > 0 && small[0] != 0) return small[0];
        return Native.LoadIcon(0, 32512 /* IDI_APPLICATION */);
    }

    public void Show()
    {
        _wanted = true;
        if (!_added) TryAdd();
    }

    public void Hide()
    {
        _wanted = false;
        StopRetry();
        if (_added) Notify(Native.NIM_DELETE);
        _added = false;
    }

    /// Adding can fail while the shell is still starting (a launch at sign-in can beat it), and then the
    /// icon would never appear, with the app closed the only way in. So a failure is tried again every 2 s
    /// for about a minute.
    private void TryAdd()
    {
        if (_hwnd == 0) return;
        _added = Notify(Native.NIM_ADD);
        if (_added) { StopRetry(); return; }
        if (_retries == 0) Native.SetTimer(_hwnd, RetryTimer, 2000, 0);
        if (++_retries > 30) StopRetry();
    }

    private void StopRetry()
    {
        _retries = 0;
        if (_hwnd != 0) Native.KillTimer(_hwnd, RetryTimer);
    }

    private bool Notify(uint message)
    {
        var data = new Native.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        return Native.Shell_NotifyIcon(message, ref data);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                switch ((uint)(lParam & 0xFFFF))
                {
                    case Native.WM_LBUTTONUP: Clicked?.Invoke(); break;
                    case Native.WM_RBUTTONUP: ShowMenu(); break;
                }
                return 0;
            }
            if (msg == Native.WM_TIMER && (nuint)wParam == RetryTimer)
            {
                if (_wanted && !_added) TryAdd();
                else StopRetry();
                return 0;
            }
            if (msg == _taskbarCreated && _wanted)
            {
                // The shell restarted and its tray is empty. Remove first so a registration that is still
                // there cannot make the add fail or show a second icon.
                if (_added) Notify(Native.NIM_DELETE);
                _added = false;
                StopRetry();
                TryAdd();
            }
        }
        catch (Exception ex) { Error?.Invoke(ex); }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = Menu?.Invoke() ?? [];
        if (items.Count == 0) return;
        var menu = Native.CreatePopupMenu();
        try
        {
            // Every clickable item gets the next command number, in menu order; submenus are filled the same way.
            var actions = new List<Action?>();
            Fill(menu, items, actions);
            Native.GetCursorPos(out var at);
            // Without this the menu stays open when the user clicks elsewhere.
            Native.SetForegroundWindow(_hwnd);
            var chosen = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, at.X, at.Y, 0, _hwnd, 0);
            Native.PostMessage(_hwnd, Native.WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= actions.Count) actions[(int)chosen - 1]?.Invoke();
        }
        finally { Native.DestroyMenu(menu); }   // also destroys the submenus attached to it
    }

    private const uint MF_POPUP = 0x00000010;

    private static void Fill(nint menu, IReadOnlyList<Item> items, List<Action?> actions)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator) { Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null); continue; }
            var grayed = item.Enabled && (item.Click is not null || item.Children is { Count: > 0 }) ? 0 : Native.MF_GRAYED;
            if (item.Children is { Count: > 0 } children)
            {
                var sub = Native.CreatePopupMenu();
                Fill(sub, children, actions);
                Native.AppendMenu(menu, Native.MF_STRING | MF_POPUP | grayed, (nuint)sub, item.Text);
                continue;
            }
            actions.Add(item.Click);
            Native.AppendMenu(menu, Native.MF_STRING | (item.Checked ? Native.MF_CHECKED : 0) | grayed, (nuint)actions.Count, item.Text);
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != 0) { Native.DestroyWindow(_hwnd); _hwnd = 0; }
        if (_icon != 0) { Native.DestroyIcon(_icon); _icon = 0; }
    }
}
