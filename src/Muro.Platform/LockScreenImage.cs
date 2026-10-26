using Microsoft.Win32;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Muro.Platform;

/// The Windows lock screen picture. Windows cannot play video on the lock screen, so Muro shows a still frame
/// of a wallpaper there. Setting it needs no package identity. These calls block: use them off the UI thread.
public static class LockScreenImage
{
    /// How the lock screen is set up: "picture", "spotlight" (Windows Spotlight) or "slideshow". Muro only
    /// replaces a picture; choosing one switches Windows to the Picture mode.
    public static string Mode()
    {
        using var content = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
        if (content?.GetValue("RotatingLockScreenEnabled") is 1) return "spotlight";
        using var lockScreen = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lock Screen");
        return lockScreen?.GetValue("SlideshowEnabled") is 1 ? "slideshow" : "picture";
    }

    /// Saves the picture on the lock screen now, to put it back later. False when there is none to save.
    public static bool SaveCurrent(string path)
    {
        try
        {
            if (LockScreen.GetImageStream() is not { } stream) return false;
            using (stream)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = stream.AsStreamForRead();
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    public static void Set(string path)
    {
        var file = StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        LockScreen.SetImageFileAsync(file).AsTask().GetAwaiter().GetResult();
    }
}
