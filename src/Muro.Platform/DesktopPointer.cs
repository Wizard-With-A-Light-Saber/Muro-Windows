using System.Runtime.InteropServices;

namespace Muro.Platform;

/// Where the pointer is and whether it is over the bare desktop, for interactive wallpapers. The wallpaper sits
/// behind the desktop icons, so it gets no mouse messages of its own: the engine samples the pointer instead, and a
/// page only reacts while the pointer is over the desktop and not over an app's window.
public static class DesktopPointer
{
    [DllImport("user32")] private static extern nint WindowFromPoint(Native.POINT p);
    [DllImport("user32")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern bool GetCursorPos(out Native.POINT p);

    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;

    public readonly record struct Sample(int X, int Y, bool OverDesktop, bool LeftDown);

    public static Sample? Read()
    {
        if (!GetCursorPos(out var p)) return null;
        var over = false;
        var hwnd = WindowFromPoint(p);
        if (hwnd != 0)
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            var cls = Native.ClassOf(root == 0 ? hwnd : root);
            // The desktop is Progman, or the WorkerW that holds the wallpaper (the icons are a child of one of them).
            over = cls is "Progman" or "WorkerW";
        }
        return new Sample(p.X, p.Y, over, (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);
    }
}
