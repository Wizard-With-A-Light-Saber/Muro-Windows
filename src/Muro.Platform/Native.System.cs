using System.Runtime.InteropServices;

namespace Muro.Platform;

// Monitors, window inspection, power, session and event hooks.
internal static partial class Native
{
    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);
    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint WM_DISPLAYCHANGE = 0x007E, WM_POWERBROADCAST = 0x0218, WM_WTSSESSION_CHANGE = 0x02B1, WM_TIMER = 0x0113, WM_APP = 0x8000;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public int Data;
    }

    [DllImport("user32")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
    [DllImport("shell32")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("user32")] public static extern nint RegisterPowerSettingNotification(nint hwnd, ref Guid setting, uint flags);
    [DllImport("wtsapi32")] public static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32")] public static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("user32")] public static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32")] public static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32")] public static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32")] public static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
}
