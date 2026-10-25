namespace Muro.Core;

/// Where Muro keeps its JSON and logs. Everything lives under %LOCALAPPDATA%\Muro
/// so the library root can be moved later without touching the settings.
public static class AppPaths
{
    /// MURO_HOME relocates everything (tests and trials use a throwaway library).
    public static string Root { get; } = Environment.GetEnvironmentVariable("MURO_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Muro");

    public static string Logs => Path.Combine(Root, "logs");

    public static string File(string name) => Path.Combine(Root, name);

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
