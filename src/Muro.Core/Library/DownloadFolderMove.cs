namespace Muro.Core.Library;

/// Moving the download folder while a wallpaper plays. Windows cannot delete a file another process has
/// open, so the old copy of the playing video would be left behind. The move therefore first takes the
/// wallpapers off the screen (the engine drops its players when the config clears), moves, and then puts
/// the same assignments back, whatever happened. Same idea as the macOS app letting go before a drive eject.
public static class DownloadFolderMove
{
    public static async Task MoveAsync(
        string root, string? picked,
        Action<int, int>? progress = null, Action<long, long>? bytes = null,
        CancellationToken ct = default, TimeSpan? settle = null)
    {
        var saved = EngineConfig.Load(root);
        var hadWallpapers = saved.AllDisplays is not null || saved.PerDisplay.Count > 0;
        var allDisplays = saved.AllDisplays;
        var perDisplay = new Dictionary<string, EngineConfig.Assignment>(saved.PerDisplay);

        if (hadWallpapers)
        {
            saved.AllDisplays = null;
            saved.PerDisplay.Clear();
            saved.Save(root);
            // Give the engine time to notice and release the files.
            await Task.Delay(settle ?? TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
        }
        try
        {
            await Task.Run(() => DownloadFolder.Move(root, picked, progress, bytes, () => ct.IsCancellationRequested), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (hadWallpapers)
            {
                // Re-read: anything else changed in the config meanwhile is kept.
                var current = EngineConfig.Load(root);
                current.AllDisplays = allDisplays;
                foreach (var (key, value) in perDisplay) current.PerDisplay[key] = value;
                current.Save(root);
            }
        }
    }
}
