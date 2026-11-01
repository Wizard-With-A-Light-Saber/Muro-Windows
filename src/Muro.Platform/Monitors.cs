using System.Runtime.InteropServices;
using Muro.Core;
using Muro.Core.Media;

namespace Muro.Platform;

/// One display. <see cref="Id"/> is the monitor's PnP interface path, which survives reboots and
/// replugging into the same port; the GDI name (\\.\DISPLAY1) is the fallback because Windows
/// renumbers it freely.
public sealed record MonitorInfo(string Id, string DeviceName, Rect Bounds, bool Primary, uint Dpi);

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint _hdc, ref Native.RECT _rc, nint _data) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(monitor, ref info)) return true;
            var r = info.rcMonitor;
            Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
            list.Add(new MonitorInfo(StableId(info.szDevice), info.szDevice,
                new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.dwFlags & 1) != 0, dpi == 0 ? 96 : dpi));
            return true;
        }, 0);
        if (list.Count > 0)
            EfficientVariant.Screen = list.Select(m => ((int)m.Bounds.Width, (int)m.Bounds.Height)).MaxBy(s => (long)s.Item1 * s.Item2);
        return list;
    }

    private static string StableId(string gdiName)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        // EDD_GET_DEVICE_INTERFACE_NAME (1) returns the monitor's device interface path.
        return Native.EnumDisplayDevices(gdiName, 0, ref dd, 1) && dd.DeviceID.Length > 0 ? dd.DeviceID : gdiName;
    }
}
