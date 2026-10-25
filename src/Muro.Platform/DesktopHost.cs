using System.Text;

namespace Muro.Platform;

/// Finds the window a wallpaper must be parented to so it sits behind the desktop icons.
///
/// Two shell layouts exist. Classic (Win10, early Win11): after message 0x052C a top-level
/// WorkerW sibling appears behind the one holding SHELLDLL_DefView. 24H2+: SHELLDLL_DefView
/// is a child of Progman and the wallpaper layer is a WorkerW child of Progman.
public static class DesktopHost
{
    public enum Layout { Classic, ProgmanChild }

    public sealed record Target(nint Parent, Layout Layout);

    public static Target? Find()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == 0) return null;

        // 0xD/1 is the variant that also works on 24H2; plain 0/0 is what older shells expect.
        Native.SendMessageTimeout(progman, 0x052C, 0xD, 0x1, Native.SMTO_NORMAL, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, 0, 0, Native.SMTO_NORMAL, 1000, out _);

        if (Native.FindWindowEx(progman, 0, "SHELLDLL_DefView", null) != 0)
        {
            var layer = Native.FindWindowEx(progman, 0, "WorkerW", null);
            return new Target(layer != 0 ? layer : progman, Layout.ProgmanChild);
        }

        nint worker = 0;
        Native.EnumWindows((h, _) =>
        {
            if (Native.FindWindowEx(h, 0, "SHELLDLL_DefView", null) != 0)
                worker = Native.FindWindowEx(0, h, "WorkerW", null);
            return true;
        }, 0);
        return worker != 0 ? new Target(worker, Layout.Classic) : null;
    }

    /// Text tree of Progman and every WorkerW, for diagnosing shell layout problems.
    public static string Dump()
    {
        var sb = new StringBuilder();
        void Walk(nint h, int depth)
        {
            Native.GetWindowRect(h, out var r);
            sb.AppendLine($"{new string(' ', depth * 2)}{Native.ClassOf(h)} 0x{h:X} vis={Native.IsWindowVisible(h)} [{r.Left},{r.Top},{r.Right},{r.Bottom}]");
            var kids = new List<nint>();
            Native.EnumChildWindows(h, (c, _) => { if (Native.GetParent(c) == h) kids.Add(c); return true; }, 0);
            foreach (var k in kids) Walk(k, depth + 1);
        }
        Native.EnumWindows((h, _) =>
        {
            var cls = Native.ClassOf(h);
            if (cls is "Progman" or "WorkerW") Walk(h, 0);
            return true;
        }, 0);
        return sb.ToString();
    }

    /// Re-applies the user's own wallpaper so the shell repaints it after we leave.
    public static void RefreshWallpaper()
    {
        var buf = new char[520];
        if (!Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)buf.Length, buf, 0)) return;
        var path = new string(buf).TrimEnd('\0');
        Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_SENDCHANGE);
    }

    /// Primary monitor rectangle in physical pixels (the process is PerMonitorV2 aware).
    public static (int X, int Y, int W, int H) PrimaryMonitor() =>
        (0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
}
