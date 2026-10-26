namespace Muro.Core.Tests;

/// config.json is written by the app, the tray, the CLI and the schedule runner, and read by the engine
/// right after every save, while a virus scanner may open it too. A file that cannot be read for a moment
/// must never turn into "defaults" that are then saved over the wallpaper assignment.
public class ConfigSafetyTests
{
    private static FileStream Hold(string path, FileShare share) => new(path, FileMode.Open, FileAccess.Read, share);

    [Fact]
    public void Update_WhileTheFileIsHeldOpen_ThrowsAndLeavesItAlone()
    {
        using var lib = new TempLibrary();
        EngineConfig.Update(lib.Root, c => c.AllDisplays = new EngineConfig.Assignment("w1"));
        var path = EngineConfig.PathIn(lib.Root);
        var before = File.ReadAllText(path);

        using (Hold(path, FileShare.None))
        {
            Assert.True(EngineConfig.Load(lib.Root).Unreadable);
            Assert.Throws<IOException>(() => EngineConfig.Update(lib.Root, c => c.ShowTrayIcon = false));
        }

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal("w1", EngineConfig.Load(lib.Root).AllDisplays!.WallpaperID);
        Assert.Empty(Directory.GetFiles(lib.Root, "*.tmp"));
    }

    [Fact]
    public void ADamagedFile_IsNeverOverwritten_ButTheEngineStillGetsDefaults()
    {
        using var lib = new TempLibrary();
        var path = EngineConfig.PathIn(lib.Root);
        File.WriteAllText(path, "{ not json");

        var loaded = EngineConfig.Load(lib.Root);
        Assert.True(loaded.Unreadable);
        Assert.True(loaded.PauseOnFullscreen);   // defaults, so the engine keeps running
        Assert.Throws<IOException>(() => EngineConfig.Update(lib.Root, c => c.ShowTrayIcon = false));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void AMissingFile_IsNotUnreadable_AndUpdateCreatesIt()
    {
        using var lib = new TempLibrary();
        Assert.False(EngineConfig.Load(lib.Root).Unreadable);
        EngineConfig.Update(lib.Root, c => c.PlaybackSpeed = 1.5);
        Assert.Equal(1.5, EngineConfig.Load(lib.Root).PlaybackSpeed);
    }

    [Fact]
    public void AShortLock_IsRiddenOut_OnBothTheReadAndTheReplace()
    {
        using var lib = new TempLibrary();
        EngineConfig.Update(lib.Root, c => c.AllDisplays = new EngineConfig.Assignment("w1"));
        var path = EngineConfig.PathIn(lib.Root);

        // Held for a moment with no sharing: Load must wait it out instead of reading nothing.
        using (var gate = new ManualResetEventSlim())
        {
            var holder = Task.Run(() => { using (Hold(path, FileShare.None)) { gate.Set(); Thread.Sleep(100); } });
            gate.Wait();
            Assert.Equal("w1", EngineConfig.Load(lib.Root).AllDisplays!.WallpaperID);
            holder.Wait();
        }

        // Held for reading only (a scanner): reading works, replacing the file does not until it lets go.
        using (var gate = new ManualResetEventSlim())
        {
            var holder = Task.Run(() => { using (Hold(path, FileShare.Read)) { gate.Set(); Thread.Sleep(100); } });
            gate.Wait();
            EngineConfig.Update(lib.Root, c => c.ShowTrayIcon = false);
            holder.Wait();
        }

        var back = EngineConfig.Load(lib.Root);
        Assert.Equal(("w1", false), (back.AllDisplays!.WallpaperID, back.ShowTrayIcon));
        Assert.Empty(Directory.GetFiles(lib.Root, "*.tmp"));
    }

    [Fact]
    public void AppSettings_FollowTheSameRule()
    {
        using var lib = new TempLibrary();
        AppSettings.Update(lib.Root, s => s.HeroId = "abc");
        var path = AppSettings.PathIn(lib.Root);
        var before = File.ReadAllText(path);

        using (Hold(path, FileShare.None))
            Assert.Throws<IOException>(() => AppSettings.Update(lib.Root, s => s.DefaultMode = "efficient"));

        Assert.Equal(before, File.ReadAllText(path));
        File.WriteAllText(path, "{ not json");
        Assert.Throws<IOException>(() => AppSettings.Update(lib.Root, s => s.DefaultMode = "efficient"));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }
}
