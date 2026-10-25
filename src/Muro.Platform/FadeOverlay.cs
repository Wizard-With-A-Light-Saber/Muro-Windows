using System.Drawing;
using System.Runtime.InteropServices;

namespace Muro.Platform;

/// A layered child window, parented behind the desktop icons, that shows one still picture (the outgoing
/// frame of a wallpaper) and can be faded out. Placed above the wallpaper window, it hides the moment
/// the next video takes over and then dissolves, which is the crossfade. GDI content with a constant
/// alpha blends reliably, unlike a Direct3D swap chain inside a layered window.
public sealed class FadeOverlay : IDisposable
{
    private const string ClassName = "MuroFadeOverlay";
    private const uint WM_PAINT = 0x000F;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 2;
    private static readonly Native.WndProc Proc = WndProc;
    private static readonly Dictionary<nint, FadeOverlay> Live = [];
    private static bool _registered;

    private Bitmap? _picture;
    public nint Handle { get; private set; }

    /// <paramref name="imagePath"/> is read into memory at once, so the file can be deleted right away.
    public FadeOverlay(DesktopHost.Target target, int x, int y, int width, int height, string imagePath)
    {
        using (var file = new Bitmap(imagePath)) _picture = new Bitmap(file);
        Register();
        var origin = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(target.Parent, ref origin);
        Handle = Native.CreateWindowEx(WS_EX_LAYERED, ClassName, "Muro fade", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
            origin.X, origin.Y, width, height, target.Parent, 0, Native.GetModuleHandle(null), 0);
        if (Handle == 0) { _picture.Dispose(); throw new InvalidOperationException("overlay window failed"); }
        Live[Handle] = this;
        Native.SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
        // Above the wallpaper window, which sits at the bottom of the same parent.
        Native.SetWindowPos(Handle, 0, origin.X, origin.Y, width, height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.InvalidateRect(Handle, 0, false);
        Native.UpdateWindow(Handle);
    }

    /// 255 shows the picture fully, 0 not at all.
    public void SetAlpha(byte alpha) => Native.SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);

    private static void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _registered = true;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == Native.WM_ERASEBKGND) return 1;
        if (msg == WM_PAINT && Live.TryGetValue(hwnd, out var overlay))
        {
            var hdc = Native.BeginPaint(hwnd, out var paint);
            try
            {
                Native.GetClientRect(hwnd, out var client);
                if (overlay._picture is not null)
                {
                    using var g = Graphics.FromHdc(hdc);
                    g.DrawImage(overlay._picture, 0, 0, client.Right, client.Bottom);
                }
            }
            finally { Native.EndPaint(hwnd, ref paint); }
            return 0;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Live.Remove(Handle);
            Native.DestroyWindow(Handle);
            Handle = 0;
        }
        _picture?.Dispose();
        _picture = null;
    }
}
