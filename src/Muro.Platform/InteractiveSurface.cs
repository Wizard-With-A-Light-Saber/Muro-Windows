using Muro.Core;

namespace Muro.Platform;

/// A plain child window over one monitor, parented like the other effect layers so it sits above the wallpaper and
/// below the desktop icons. An interactive wallpaper's WebView2 is created inside it. Create and dispose it on the
/// thread that owns the WebView2 (the window belongs to the thread that made it).
public sealed class InteractiveSurface : IDisposable
{
    private const string ClassName = "MuroInteractiveSurface";
    private static readonly Native.WndProc Proc = (h, m, w, l) =>
        m == Native.WM_ERASEBKGND ? 1 : Native.DefWindowProc(h, m, w, l);
    private static bool _registered;

    public nint Handle { get; private set; }
    public int Width { get; }
    public int Height { get; }
    private readonly nint _parent;

    public InteractiveSurface(DesktopHost.Target target, Rect screen)
    {
        Register();
        _parent = target.Parent;
        Width = (int)screen.Width;
        Height = (int)screen.Height;
        var origin = new Native.POINT { X = (int)screen.X, Y = (int)screen.Y };
        Native.ScreenToClient(target.Parent, ref origin);
        Handle = Native.CreateWindowEx(0, ClassName, "Muro interactive", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
            origin.X, origin.Y, Width, Height, target.Parent, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) throw new InvalidOperationException("interactive window failed");
        Native.SetWindowPos(Handle, 0, origin.X, origin.Y, Width, Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// False once the shell recreated its wallpaper layer (the old parent is gone and the window with it).
    public bool IsAlive => Handle != 0 && Native.IsWindow(Handle) && Native.GetParent(Handle) == _parent;

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

    public void Dispose()
    {
        if (Handle == 0) return;
        Native.DestroyWindow(Handle);
        Handle = 0;
    }
}
