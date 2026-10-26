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
    public sealed record Item(string Text, Action? Click = null, bool Checked = false, bool Enabled = true)
    {
        public static readonly Item Separator = new("-");
        public bool IsSeparator => ReferenceEquals(this, Separator);
    }

    private const string ClassName = "MuroEngineTray";
    private const uint CallbackMessage = Native.WM_APP + 1;

    private readonly Native.WndProc _proc;
    private readonly string _tooltip;
    private readonly uint _taskbarCreated;
    private nint _hwnd, _icon;
    private bool _visible;

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
        if (_visible || _hwnd == 0) return;
        _visible = Notify(Native.NIM_ADD);
    }

    public void Hide()
    {
        if (!_visible) return;
        Notify(Native.NIM_DELETE);
        _visible = false;
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
            if (msg == _taskbarCreated && _visible) Notify(Native.NIM_ADD);
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
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsSeparator) { Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null); continue; }
                var flags = Native.MF_STRING | (item.Checked ? Native.MF_CHECKED : 0) | (item.Enabled && item.Click is not null ? 0 : Native.MF_GRAYED);
                Native.AppendMenu(menu, flags, (nuint)(i + 1), item.Text);
            }
            Native.GetCursorPos(out var at);
            // Without this the menu stays open when the user clicks elsewhere.
            Native.SetForegroundWindow(_hwnd);
            var chosen = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, at.X, at.Y, 0, _hwnd, 0);
            Native.PostMessage(_hwnd, Native.WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= items.Count) items[(int)chosen - 1].Click?.Invoke();
        }
        finally { Native.DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != 0) { Native.DestroyWindow(_hwnd); _hwnd = 0; }
        if (_icon != 0) { Native.DestroyIcon(_icon); _icon = 0; }
    }
}
