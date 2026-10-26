using Muro.Core.Library;
using Muro.Core.Media;
using Muro.Core.Schedule;

namespace Muro.Core.Tests;

public class SurfaceTests
{
    [Fact]
    public void ScreenSaverAndLockScreen_AssignmentsRoundTrip_AndStartEmpty()
    {
        using var lib = new TempLibrary();
        var fresh = EngineConfig.Load(lib.Root);
        Assert.Null(fresh.ScreenSaver);
        Assert.Null(fresh.LockScreen);

        EngineConfig.Update(lib.Root, c => { c.ScreenSaver = new("s1", "efficient"); c.LockScreen = new("l1"); });
        var back = EngineConfig.Load(lib.Root);
        Assert.Equal(("s1", true, "l1"), (back.ScreenSaver!.WallpaperID, back.ScreenSaver.IsEfficient, back.LockScreen!.WallpaperID));
    }

    [Fact]
    public void Deleting_AWallpaper_ClearsItFromTheScreenSaverAndLockScreen_ButKeepsOthers()
    {
        using var lib = new TempLibrary();
        EngineConfig.Update(lib.Root, c => { c.ScreenSaver = new("gone"); c.LockScreen = new("kept"); });
        var result = WallpaperDeletion.Delete(lib.Root, new HashSet<string> { "gone" });
        Assert.True(result.ClearedDesktop);
        var config = EngineConfig.Load(lib.Root);
        Assert.Null(config.ScreenSaver);
        Assert.Equal("kept", config.LockScreen!.WallpaperID);
    }

    [Fact]
    public void ScheduleState_IsKeptPerSurface()
    {
        using var lib = new TempLibrary();
        ScheduleControl.StartPlaylist(lib.Root, Surface.ScreenSaver, "pS");
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "pD");
        Assert.Equal("pS", ScheduleStore.Load(lib.Root, Surface.ScreenSaver).PlaylistID);
        Assert.Equal("pD", ScheduleStore.Load(lib.Root, Surface.Desktop).PlaylistID);
        Assert.False(ScheduleStore.Load(lib.Root, Surface.LockScreen).IsActive);

        // Stopping one leaves the others, and only the desktop has a wallpaper to go back to.
        ScheduleControl.Stop(lib.Root, Surface.ScreenSaver);
        Assert.False(ScheduleStore.Load(lib.Root, Surface.ScreenSaver).IsActive);
        Assert.True(ScheduleStore.Load(lib.Root, Surface.Desktop).IsActive);
        Assert.Null(ScheduleStore.Load(lib.Root, Surface.ScreenSaver).Before);
    }

    [Theory]
    [InlineData(0, "Never")]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(7200, "2 hours")]
    [InlineData(45, "45 seconds")]
    public void DelayLabels(int seconds, string label) => Assert.Equal(label, ScreenSaverDelay.Label(seconds));

    [Fact]
    public void Stills_LiveUnderTheLibrary_AndAreNotSweptWithTheVideos()
    {
        using var lib = new TempLibrary();
        var path = Stills.PathFor(lib.Root, "abc");
        Assert.Equal(Path.Combine(lib.Root, "Stills", "abc.jpg"), path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        LibraryWriter.SweepOrphans(lib.Root);
        Assert.True(File.Exists(path));

        // Deleting the wallpaper takes its still with it.
        WallpaperDeletion.Delete(lib.Root, new HashSet<string> { "abc" });
        Assert.False(File.Exists(path));
    }
}
