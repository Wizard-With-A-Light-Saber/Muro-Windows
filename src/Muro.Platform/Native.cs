using System.Runtime.InteropServices;

namespace Muro.Platform;

internal static class Native
{
    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint SMTO_NORMAL = 0;
    public const uint SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004, SWP_SHOWWINDOW = 0x0040;
    public const uint SPI_GETDESKWALLPAPER = 0x0073, SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    public static readonly nint HWND_BOTTOM = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    [DllImport("user32")] public static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc p, nint l);
    [DllImport("user32")] public static extern bool EnumChildWindows(nint parent, EnumWindowsProc p, nint l);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint h, char[] buf, int max);
    [DllImport("user32")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32")] public static extern nint GetParent(nint h);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] public static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);
    [DllImport("user32")] public static extern bool DestroyWindow(nint h);
    [DllImport("user32")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern bool ScreenToClient(nint h, ref POINT p);
    [DllImport("user32")] public static extern int GetMessage(out MSG m, nint h, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern nint DispatchMessage(ref MSG m);
    [DllImport("user32")] public static extern bool PostThreadMessage(uint thread, uint msg, nint w, nint l);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, char[] pv, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, string pv, uint flags);
    [DllImport("user32")] public static extern int GetSystemMetrics(int index);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    public static string ClassOf(nint h)
    {
        var b = new char[256];
        int n = GetClassName(h, b, b.Length);
        return new string(b, 0, n);
    }
}
