using Muro.Core;

namespace Muro.Engine;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine(line);
        try
        {
            lock (Gate) File.AppendAllText(Path.Combine(AppPaths.Logs, "engine.log"), line + Environment.NewLine);
        }
        catch { /* logging must never take the wallpaper down */ }
    }
}
