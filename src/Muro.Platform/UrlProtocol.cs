using Microsoft.Win32;

namespace Muro.Platform;

/// Makes Windows open <c>muro://</c> links with the engine, per user (no admin rights). Written only when it
/// differs, so starting the engine does not touch the registry every time. The installer removes the key again.
public static class UrlProtocol
{
    private const string Key = @"Software\Classes\muro";

    public static void Register(string exePath)
    {
        var command = "\"" + exePath + "\" \"%1\"";
        using (var current = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command"))
            if (current?.GetValue(null) as string == command) return;
        using var root = Registry.CurrentUser.CreateSubKey(Key);
        root.SetValue(null, "URL:Muro");
        root.SetValue("URL Protocol", "");
        using var open = root.CreateSubKey(@"shell\open\command");
        open.SetValue(null, command);
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
}
