using System.Runtime.InteropServices;
using Muro.Core;

namespace Muro.Platform;

/// What app windows are open, reduced to position and size. Ignores the shell's own windows,
/// minimised, cloaked (other virtual desktop, suspended UWP) and tool windows, and untitled
/// helper windows.
public static class ShellWindows
{
    public readonly record struct AppWindow(nint Handle, Rect Bounds, bool HasCaption);

    private static readonly HashSet<string> ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "MuroEngineEvents",
    ];

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    public static List<AppWindow> Enumerate()
    {
        var result = new List<AppWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            if (((uint)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.GetWindowTextLength(hwnd) == 0) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (ShellClasses.Contains(Native.ClassOf(hwnd))) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == OwnPid) return true;

            // The frame bounds exclude the invisible resize border Windows adds around a window.
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) != 0
                && !Native.GetWindowRect(hwnd, out r)) return true;
            var hasCaption = ((uint)Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE) & Native.WS_CAPTION) == Native.WS_CAPTION;
            result.Add(new AppWindow(hwnd, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), hasCaption));
            return true;
        }, 0);
        return result;
    }

    /// Monitors showing a full-screen app: a borderless window at least as big as the monitor
    /// (games, video players, F11 browsers), or the foreground app when Windows itself reports
    /// busy / D3D full screen / presentation mode.
    public static HashSet<string> FullscreenMonitors(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<AppWindow> windows)
    {
        var result = new HashSet<string>();
        foreach (var w in windows.Where(w => !w.HasCaption))
            foreach (var m in monitors)
                if (w.Bounds.X <= m.Bounds.X + 1 && w.Bounds.Y <= m.Bounds.Y + 1
                    && w.Bounds.Right >= m.Bounds.Right - 1 && w.Bounds.Bottom >= m.Bounds.Bottom - 1)
                    result.Add(m.Id);

        // 2 = busy, 3 = running D3D full screen, 4 = presentation mode.
        if (Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4)
        {
            var foreground = Native.GetForegroundWindow();
            // With the bare desktop (or the taskbar) focused, Windows reports "busy" because that window is as
            // big as the screen. It is not an app: pausing then freezes the wallpaper exactly when the desktop
            // is clear and being looked at.
            if (foreground != 0 && !ShellClasses.Contains(Native.ClassOf(foreground)))
            {
                var handle = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
                var info = new Native.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (handle != 0 && Native.GetMonitorInfo(handle, ref info))
                    foreach (var m in monitors.Where(m => m.DeviceName == info.szDevice)) result.Add(m.Id);
            }
        }
        return result;
    }
}
