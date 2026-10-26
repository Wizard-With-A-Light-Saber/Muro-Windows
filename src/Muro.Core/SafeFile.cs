namespace Muro.Core;

internal static class SafeFile
{
    /// Writes <paramref name="content"/> to a sibling with a name of its own, then replaces the file with it,
    /// so a reader never sees half a file and two writers cannot trip over one temp name. The replace is tried
    /// again briefly when a reader (a virus scanner, the engine's watcher) has the file open: Windows then
    /// answers with a sharing violation or with "access denied". Any failure comes out as an
    /// <see cref="IOException"/>, so callers handle one kind of error.
    public static void WriteAtomically(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 4) throw new IOException(Path.GetFileName(path) + " could not be saved: " + ex.Message, ex);
                    Thread.Sleep(50);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(Path.GetFileName(path) + " could not be saved: " + ex.Message, ex);
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
