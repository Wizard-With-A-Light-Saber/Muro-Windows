// Win32 declarations for the notification-area icon and its popup menu.
using System.Runtime.InteropServices;

namespace Muro.Platform;

internal static partial class Native
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const uint WM_NULL = 0, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x0800;
    public const uint TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint count);
    [DllImport("user32")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32")] public static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32")] public static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] public static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT point);
}
