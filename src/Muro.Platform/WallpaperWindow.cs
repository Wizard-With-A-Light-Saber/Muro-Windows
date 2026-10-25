namespace Muro.Platform;

/// A child window parented behind the desktop icons, covering exactly one monitor rectangle
/// (virtual-desktop pixel coordinates). The playback engine renders into <see cref="Handle"/>.
public sealed class WallpaperWindow : IDisposable
{
    private const string ClassName = "MuroWallpaperHost";
    private static readonly Native.WndProc Proc = (h, m, w, l) =>
        m == Native.WM_ERASEBKGND ? 1 : Native.DefWindowProc(h, m, w, l);
    private static bool _registered;

    private readonly DesktopHost.Target _target;
    public nint Handle { get; private set; }

    public WallpaperWindow(DesktopHost.Target target, int x, int y, int width, int height)
    {
        _target = target;
        Register();
        var origin = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(target.Parent, ref origin);
        Handle = Native.CreateWindowEx(0, ClassName, "Muro", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
            origin.X, origin.Y, width, height, target.Parent, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) throw new InvalidOperationException("CreateWindowEx failed");
        // Behind SHELLDLL_DefView when we share Progman with it; harmless in the classic layout.
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, origin.X, origin.Y, width, height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _registered = true;
    }

    /// Moves and resizes to a new monitor rectangle (virtual-desktop pixels). mpv's own child
    /// window does not follow its parent, so it is resized by hand.
    public void Move(int x, int y, int width, int height)
    {
        if (Handle == 0) return;
        var origin = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(_target.Parent, ref origin);
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, origin.X, origin.Y, width, height, Native.SWP_NOACTIVATE);
        Native.EnumChildWindows(Handle, (child, _) =>
        {
            Native.SetWindowPos(child, 0, 0, 0, width, height, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
            return true;
        }, 0);
    }

    /// False once the shell has recreated its wallpaper layer (resolution change, Explorer restart):
    /// the old parent is gone and our window went with it.
    public bool IsAlive => Handle != 0 && Native.IsWindow(Handle) && Native.GetParent(Handle) == _target.Parent;

    public void Dispose()
    {
        if (Handle == 0) return;
        Native.DestroyWindow(Handle);
        Handle = 0;
    }
}
