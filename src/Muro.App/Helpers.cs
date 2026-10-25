using Microsoft.UI.Xaml;

namespace Muro.App;

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1073741824L => (bytes / 1073741824.0).ToString("0.0") + " GB",
        >= 1048576L => (bytes / 1048576.0).ToString("0.#") + " MB",
        >= 1024L => (bytes / 1024.0).ToString("0") + " KB",
        _ => bytes + " B",
    };

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static string Resolution(int width, int height, double fps) =>
        width + "x" + height + "  " + (int)Math.Round(fps) + " fps";
}

/// Small append-only log for the app, next to the engine log. Never throws.
internal static class AppLog
{
    public static void Info(string message)
    {
        try { File.AppendAllText(Path.Combine(Muro.Core.AppPaths.Logs, "app.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine); }
        catch { }
    }
}
