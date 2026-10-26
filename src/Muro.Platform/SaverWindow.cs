using System.Runtime.InteropServices;

namespace Muro.Platform;

/// A black full-monitor top-level window for the screen saver. The player renders into <see cref="Handle"/>.
public sealed class SaverWindow : IDisposable
{
    private const string ClassName = "MuroScreenSaver";
    private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
    private static bool _registered;
    private static readonly Native.WndProc Proc = Native.DefWindowProc;   // kept alive for the window class

    [DllImport("gdi32")] private static extern nint GetStockObject(int index);

    public nint Handle { get; private set; }

    public SaverWindow(int x, int y, int width, int height)
    {
        Register();
        Handle = Native.CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName, "Muro screen saver", WS_POPUP | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN,
            x, y, width, height, 0, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) throw new InvalidOperationException("CreateWindowEx failed");
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null),
            hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _registered = true;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Native.DestroyWindow(Handle);
        Handle = 0;
    }
}

/// Reports the first real input after the screen saver started: a key, a mouse button or wheel, or the mouse
/// moving more than a few pixels. Low-level hooks, because the video is drawn by a child window that
/// swallows mouse messages before the saver window would see them. Needs the message loop on this thread.
public sealed class IdleInputWatcher : IDisposable
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32")] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int MoveSlack = 10;
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(700);

    private readonly HookProc _keyboard, _mouse;
    private readonly nint _keyboardHook, _mouseHook;
    private readonly DateTime _since = DateTime.UtcNow;
    private Native.POINT _origin;
    private bool _raised;

    public event Action? Activity;

    /// False when Windows refused a hook: the screen saver could then never be ended by the user.
    public bool Installed => _keyboardHook != 0 && _mouseHook != 0;

    public IdleInputWatcher()
    {
        Native.GetCursorPos(out _origin);
        _keyboard = (code, w, l) => { if (code >= 0 && (int)w is 0x100 or 0x104) Raise(); return CallNextHookEx(0, code, w, l); };
        _mouse = (code, w, l) =>
        {
            if (code >= 0)
            {
                var message = (int)w;
                if (message == 0x200 /* WM_MOUSEMOVE */)
                {
                    var p = Marshal.PtrToStructure<Native.POINT>(l);
                    if (Math.Abs(p.X - _origin.X) + Math.Abs(p.Y - _origin.Y) > MoveSlack) Raise();
                }
                else if (message is >= 0x201 and <= 0x20E) Raise();   // buttons and wheel
            }
            return CallNextHookEx(0, code, w, l);
        };
        var module = Native.GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboard, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouse, module, 0);
    }

    private void Raise()
    {
        if (_raised || DateTime.UtcNow - _since < Grace) return;
        _raised = true;
        Activity?.Invoke();
    }

    public void Dispose()
    {
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
    }
}
