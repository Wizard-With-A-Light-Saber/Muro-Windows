using Muro.Core.Library;
using Muro.Core.Schedule;

namespace Muro.Core.Tests;

public class PlaylistTests
{
    [Fact]
    public void Playlists_RoundTripInTheMacShape()
    {
        using var lib = new TempLibrary();
        PlaylistStore.Save(lib.Root, [new Playlist { Id = "p1", Name = "Evening", WallpaperIDs = ["a", "b"], IntervalMinutes = 15, Shuffle = true }]);
        var text = File.ReadAllText(PlaylistStore.PathIn(lib.Root));
        Assert.Contains("\"wallpaperIDs\"", text);
        Assert.Contains("\"intervalMinutes\": 15", text);
        var back = Assert.Single(PlaylistStore.Load(lib.Root));
        Assert.Equal(("p1", "Evening", 15, true), (back.Id, back.Name, back.IntervalMinutes, back.Shuffle));
        Assert.Equal(["a", "b"], back.WallpaperIDs);
    }

    [Fact]
    public void PruningRemovesDeletedIds_AndReportsPlaylistsLeftEmpty()
    {
        var playlists = new List<Playlist>
        {
            new() { Id = "mixed", WallpaperIDs = ["a", "b"] },
            new() { Id = "doomed", WallpaperIDs = ["a"] },
            new() { Id = "empty", WallpaperIDs = [] },   // already empty: not newly emptied
        };
        var (pruned, emptied) = PlaylistStore.Pruned(playlists, new HashSet<string> { "a" });
        Assert.Equal(["b"], pruned[0].WallpaperIDs);
        Assert.Empty(pruned[1].WallpaperIDs);
        Assert.Equal(["doomed"], emptied);
    }

    [Fact]
    public void UniqueNames_TakeTheFirstFreeNumber_IgnoringCaseAndSpacing()
    {
        Assert.Equal("New Playlist", Names.Unique("New Playlist", []));
        Assert.Equal("New Playlist", Names.Unique("New Playlist", ["Evening"]));
        Assert.Equal("New Playlist 2", Names.Unique("New Playlist", ["New Playlist"]));
        Assert.Equal("New Playlist 3", Names.Unique("New Playlist", ["New Playlist", "New Playlist 2"]));
        Assert.Equal("New Playlist 2", Names.Unique("New Playlist", ["New Playlist", "New Playlist 3"]));
        Assert.Equal("New Playlist 2", Names.Unique("New Playlist", ["  new playlist "]));
    }

    [Fact]
    public void NameClashes_IgnoreCaseSpacingAndTheRowBeingEdited()
    {
        var existing = new[] { ("a", "Evening"), ("b", "Morning") };
        Assert.True(Names.IsTaken("evening", existing, null));
        Assert.True(Names.IsTaken("  Evening  ", existing, null));
        Assert.False(Names.IsTaken("Night", existing, null));
        Assert.False(Names.IsTaken("Evening", existing, "a"));
        Assert.True(Names.IsTaken("Morning", existing, "a"));
        Assert.False(Names.IsTaken("   ", existing, null));
    }

    // ---- The delete cascade, end to end

    private static void Library(TempLibrary lib, params string[] ids)
    {
        foreach (var id in ids) lib.Write("Masters/" + id + ".mov");
        LibraryWriter.Update(lib.Root, m => m.Wallpapers.AddRange(ids.Select(id => TempLibrary.Entry(id))));
    }

    [Fact]
    public void Deleting_PrunesPlaylistsAndAutomations_AndStopsOnesLeftEmpty()
    {
        using var lib = new TempLibrary();
        Library(lib, "a", "b", "c");
        PlaylistStore.Save(lib.Root, [
            new Playlist { Id = "p-mixed", Name = "Mixed", WallpaperIDs = ["a", "b"] },
            new Playlist { Id = "p-only-a", Name = "Only A", WallpaperIDs = ["a"] }]);
        AutomationStore.Save(lib.Root, [new Automation
        {
            Id = "day", Name = "Day", Mode = AutomationMode.Clock,
            Steps = [new() { Id = "1", WallpaperID = "a", StartMinute = 0, EndMinute = 720 }, new() { Id = "2", WallpaperID = "c", StartMinute = 720, EndMinute = 0 }],
        }]);
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p-only-a");

        var result = WallpaperDeletion.Delete(lib.Root, new HashSet<string> { "a" });

        Assert.Equal(["b"], PlaylistStore.Load(lib.Root).Single(p => p.Id == "p-mixed").WallpaperIDs);
        Assert.Empty(PlaylistStore.Load(lib.Root).Single(p => p.Id == "p-only-a").WallpaperIDs);
        var day = AutomationStore.Load(lib.Root).Single();
        Assert.Equal(["c"], day.Steps.Select(s => s.WallpaperID));
        Assert.True(Automation.IsAllDay(day.Steps[0]));        // the survivor takes the whole day
        Assert.False(ScheduleStore.Load(lib.Root, Surface.Desktop).IsActive);   // the running one was emptied: stopped
        Assert.Equal(["Only A"], result.StoppedSchedules);
        Assert.Equal(["b", "c"], result.Manifest.Wallpapers.Select(w => w.Id).Order());
    }

    [Fact]
    public void Deleting_LeavesARunningScheduleRunning_WhenItStillHasWallpapers()
    {
        using var lib = new TempLibrary();
        Library(lib, "a", "b");
        PlaylistStore.Save(lib.Root, [new Playlist { Id = "p", Name = "P", WallpaperIDs = ["a", "b"] }]);
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p");

        var result = WallpaperDeletion.Delete(lib.Root, new HashSet<string> { "a" });

        Assert.Empty(result.StoppedSchedules);
        Assert.True(ScheduleStore.Load(lib.Root, Surface.Desktop).IsActive);
    }

    // ---- Starting and stopping

    [Fact]
    public void Start_RemembersWhatWasShowing_AndStopPutsItBack()
    {
        using var lib = new TempLibrary();
        Library(lib, "mine", "x");
        new EngineConfig { AllDisplays = new("mine", "efficient") }.Save(lib.Root);

        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p");
        var state = ScheduleStore.Load(lib.Root, Surface.Desktop);
        Assert.True(state.PendingStart);
        Assert.Equal("mine", state.Before!.WallpaperID);

        // The schedule takes the desktop over.
        var config = EngineConfig.Load(lib.Root);
        config.AllDisplays = new("x");
        config.Save(lib.Root);

        ScheduleControl.Stop(lib.Root, Surface.Desktop);
        Assert.False(ScheduleStore.Load(lib.Root, Surface.Desktop).IsActive);
        Assert.Equal(("mine", "efficient"), (EngineConfig.Load(lib.Root).AllDisplays!.WallpaperID, EngineConfig.Load(lib.Root).AllDisplays!.Mode));
    }

    [Fact]
    public void StartingAnotherSchedule_KeepsTheOriginalBefore()
    {
        using var lib = new TempLibrary();
        Library(lib, "mine");
        new EngineConfig { AllDisplays = new("mine") }.Save(lib.Root);
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p1");
        var config = EngineConfig.Load(lib.Root); config.AllDisplays = new("other"); config.Save(lib.Root);
        ScheduleControl.StartAutomation(lib.Root, Surface.Desktop, "a1");
        var state = ScheduleStore.Load(lib.Root, Surface.Desktop);
        Assert.Equal("a1", state.AutomationID);
        Assert.Null(state.PlaylistID);
        Assert.Equal("mine", state.Before!.WallpaperID);
    }

    [Fact]
    public void StopWithoutRestore_LeavesTheDesktopAlone_ForAManualApply()
    {
        using var lib = new TempLibrary();
        Library(lib, "mine", "chosen");
        new EngineConfig { AllDisplays = new("mine") }.Save(lib.Root);
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p");
        var config = EngineConfig.Load(lib.Root); config.AllDisplays = new("chosen"); config.Save(lib.Root);

        ScheduleControl.Stop(lib.Root, Surface.Desktop, restore: false);

        Assert.Equal("chosen", EngineConfig.Load(lib.Root).AllDisplays!.WallpaperID);
    }

    [Fact]
    public void StopRestore_SkipsAWallpaperThatWasDeletedMeanwhile()
    {
        using var lib = new TempLibrary();
        new EngineConfig { AllDisplays = new("gone") }.Save(lib.Root);   // not in the library
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "p");
        var config = EngineConfig.Load(lib.Root); config.AllDisplays = null; config.Save(lib.Root);
        ScheduleControl.Stop(lib.Root, Surface.Desktop);
        Assert.Null(EngineConfig.Load(lib.Root).AllDisplays);
    }

    [Fact]
    public void ScheduleState_SurfacesAreIndependent()
    {
        using var lib = new TempLibrary();
        ScheduleControl.StartPlaylist(lib.Root, Surface.Desktop, "d");
        ScheduleControl.StartAutomation(lib.Root, Surface.LockScreen, "l");
        Assert.Equal("d", ScheduleStore.Load(lib.Root, Surface.Desktop).PlaylistID);
        Assert.Equal("l", ScheduleStore.Load(lib.Root, Surface.LockScreen).AutomationID);
        Assert.False(ScheduleStore.Load(lib.Root, Surface.ScreenSaver).IsActive);
        ScheduleControl.Stop(lib.Root, Surface.Desktop, restore: false);
        Assert.True(ScheduleStore.Load(lib.Root, Surface.LockScreen).IsActive);
    }
}
