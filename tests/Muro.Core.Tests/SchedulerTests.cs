using Muro.Core.Schedule;

namespace Muro.Core.Tests;

public class SchedulerTests
{
    /// A clock the tests move by hand. UTC drives deadlines; Local drives the minute of the day.
    private sealed class Clock
    {
        public DateTime Utc = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        public DateTime Local = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Local);
        public void Advance(TimeSpan span) { Utc += span; Local += span; }
        public void SetLocal(int hour, int minute, int second = 0) => Local = new DateTime(2026, 10, 26, hour, minute, second, DateTimeKind.Local);
    }

    private sealed class Harness
    {
        public readonly Clock Time = new();
        public readonly Scheduler Scheduler;
        public readonly List<string> Applied = [];
        public string? Current;
        public int Changes;
        public Func<int, int> Pick = _ => 0;

        public Harness()
        {
            Scheduler = new Scheduler(() => Time.Utc, () => Time.Local, n => Pick(n))
            {
                CurrentId = () => Current,
                Apply = id => { Applied.Add(id); Current = id; },
                Changed = () => Changes++,
            };
        }
    }

    private static Playlist Playlist(string id, int minutes, bool shuffle, params string[] ids) =>
        new() { Id = id, Name = id, IntervalMinutes = minutes, Shuffle = shuffle, WallpaperIDs = ids.ToList() };

    private static Automation Timer(string id, params (string Wallpaper, int Seconds)[] steps) => new()
    {
        Id = id, Name = id, Mode = AutomationMode.Timer,
        Steps = steps.Select(s => new Step { WallpaperID = s.Wallpaper, Seconds = s.Seconds }).ToList(),
    };

    private static Automation ClockDay(string id, params (string Wallpaper, int Start, int End)[] windows) => new()
    {
        Id = id, Name = id, Mode = AutomationMode.Clock,
        Steps = windows.Select(w => new Step { WallpaperID = w.Wallpaper, StartMinute = w.Start, EndMinute = w.End }).ToList(),
    };

    // ---- Playlists

    [Fact]
    public void Playlist_AppliesTheFirstAtOnce_ThenWalksInOrderAndWraps()
    {
        var h = new Harness();
        var playlist = Playlist("p", 10, false, "a", "b", "c");
        h.Scheduler.Update([], [playlist]);

        h.Scheduler.StartPlaylist(playlist);
        Assert.Equal(["a"], h.Applied);
        Assert.Equal(h.Time.Utc.AddMinutes(10), h.Scheduler.NextWakeUtc);

        foreach (var expected in new[] { "b", "c", "a", "b" })
        {
            h.Time.Advance(TimeSpan.FromMinutes(10));
            h.Scheduler.Fire();
            Assert.Equal(expected, h.Current);
        }
    }

    [Theory]
    [InlineData(8, 7, 30, 8, 30)]    // every 30 minutes lands on :00 and :30
    [InlineData(8, 30, 30, 9, 0)]    // exactly on a boundary: the next one
    [InlineData(8, 7, 60, 9, 0)]     // every hour is on the hour
    [InlineData(23, 55, 30, 24, 0)]  // the count restarts at midnight
    [InlineData(8, 0, 7, 8, 3)]      // 7 does not divide the day: boundaries at 476 and 483 minutes after midnight
    public void Playlist_ChangesOnTheClock(int hour, int minute, int every, int wakeHour, int wakeMinute)
    {
        var h = new Harness();
        h.Time.SetLocal(hour, minute);
        h.Time.Utc = new DateTime(2026, 10, 26, hour, minute, 0, DateTimeKind.Utc);
        var playlist = Playlist("p", every, false, "a", "b");
        h.Scheduler.Update([], [playlist]);

        h.Scheduler.StartPlaylist(playlist);

        Assert.Equal(h.Time.Utc.Date.AddHours(wakeHour).AddMinutes(wakeMinute), h.Scheduler.NextWakeUtc);
    }

    /// Re-evaluating for an unrelated reason must never skip an entry: only a passed deadline advances.
    [Fact]
    public void Playlist_ReevaluateBeforeTheDeadline_DoesNotSkipAnything()
    {
        var h = new Harness();
        var playlist = Playlist("p", 10, false, "a", "b", "c");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);

        h.Time.Advance(TimeSpan.FromMinutes(4));
        for (var i = 0; i < 5; i++) h.Scheduler.Reevaluate();
        Assert.Equal(["a"], h.Applied);

        h.Time.Advance(TimeSpan.FromMinutes(7));   // past the deadline, as after a sleep
        h.Scheduler.Reevaluate();
        Assert.Equal(["a", "b"], h.Applied);
        Assert.Equal(new DateTime(2026, 10, 26, 8, 20, 0, DateTimeKind.Utc), h.Scheduler.NextWakeUtc);   // the next boundary on the clock (it is 08:11 now)
    }

    [Fact]
    public void Playlist_ManualNextAndPrevious_WrapBothWays()
    {
        var h = new Harness();
        var playlist = Playlist("p", 30, false, "a", "b", "c");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);

        h.Scheduler.Advance(forward: false);
        Assert.Equal("c", h.Current);
        h.Scheduler.Advance(forward: true);
        Assert.Equal("a", h.Current);
    }

    [Fact]
    public void Playlist_WhenTheCurrentWallpaperIsNotInTheList_StartsFromTheEnds()
    {
        var h = new Harness { Current = "something-else" };
        var playlist = Playlist("p", 30, false, "a", "b", "c");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.Restore(null, "p", 0, h.Time.Utc);
        h.Scheduler.Advance(forward: true);
        Assert.Equal("a", h.Current);
        h.Current = "something-else";
        h.Scheduler.Advance(forward: false);
        Assert.Equal("c", h.Current);
    }

    [Fact]
    public void Shuffle_NeverRepeatsTheCurrentWallpaper_AndUsesTheRandomSource()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, true, "a", "b", "c");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        Assert.Equal("a", h.Current);

        h.Pick = n => n - 1;                       // always the last candidate
        h.Scheduler.Advance(true);
        Assert.Equal("c", h.Current);              // candidates were b, c
        h.Scheduler.Advance(true);
        Assert.Equal("b", h.Current);              // candidates were a, b
        for (var i = 0; i < 20; i++)
        {
            var before = h.Current;
            h.Pick = n => Random.Shared.Next(n);
            h.Scheduler.Advance(true);
            Assert.NotEqual(before, h.Current);
        }
    }

    [Fact]
    public void Shuffle_OfASingleWallpaperJustStays()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, true, "only");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        h.Scheduler.Advance(true);
        Assert.Equal("only", h.Current);
    }

    [Fact]
    public void AnEmptyPlaylistCannotBeStarted()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, false);
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        Assert.False(h.Scheduler.IsRunning);
        Assert.Empty(h.Applied);
    }

    // ---- Timer automations

    [Fact]
    public void TimerAutomation_GivesEveryWallpaperItsOwnLength_AndCycles()
    {
        var h = new Harness();
        var automation = Timer("t", ("a", 10), ("b", 20), ("c", 30));
        h.Scheduler.Update([automation], []);
        h.Scheduler.StartAutomation(automation);

        Assert.Equal("a", h.Current);
        Assert.Equal(h.Time.Utc.AddSeconds(10), h.Scheduler.NextWakeUtc);
        h.Time.Advance(TimeSpan.FromSeconds(10)); h.Scheduler.Fire();
        Assert.Equal("b", h.Current);
        Assert.Equal(h.Time.Utc.AddSeconds(20), h.Scheduler.NextWakeUtc);
        h.Time.Advance(TimeSpan.FromSeconds(20)); h.Scheduler.Fire();
        Assert.Equal("c", h.Current);
        h.Time.Advance(TimeSpan.FromSeconds(30)); h.Scheduler.Fire();
        Assert.Equal("a", h.Current);   // wraps to the top
    }

    /// A PC that slept through three steps must wake showing the right wallpaper, not carry on from where it dozed off.
    [Fact]
    public void TimerAutomation_CatchesUpThroughStepsThatElapsedWhileAsleep()
    {
        var h = new Harness();
        var automation = Timer("t", ("a", 60), ("b", 60), ("c", 60));
        h.Scheduler.Update([automation], []);
        h.Scheduler.StartAutomation(automation);

        h.Time.Advance(TimeSpan.FromSeconds(60 * 3 + 60 + 20));   // one full cycle, then 80 s into the next: step b, 20 s in
        h.Scheduler.Reevaluate();

        Assert.Equal("b", h.Current);
        Assert.Equal(1, h.Scheduler.StepIndex);
        Assert.Equal(h.Time.Utc.AddSeconds(-20), h.Scheduler.StepStartedAtUtc);
        Assert.Equal(h.Time.Utc.AddSeconds(40), h.Scheduler.NextWakeUtc);
    }

    [Fact]
    public void TimerAutomation_ReevaluateWithinAStep_ChangesNothing()
    {
        var h = new Harness();
        var automation = Timer("t", ("a", 60), ("b", 60));
        h.Scheduler.Update([automation], []);
        h.Scheduler.StartAutomation(automation);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Scheduler.Reevaluate();
        Assert.Equal(["a"], h.Applied);
        Assert.Equal(0, h.Scheduler.StepIndex);
    }

    [Fact]
    public void ARestartedEngine_ResumesInTheRightStep()
    {
        var h = new Harness();
        var automation = Timer("t", ("a", 100), ("b", 100));
        h.Scheduler.Update([automation], []);
        // Persisted: step b, started 40 seconds ago.
        h.Scheduler.Restore("t", null, 1, h.Time.Utc.AddSeconds(-40));
        h.Scheduler.Reevaluate();
        Assert.Equal("b", h.Current);
        Assert.Equal(h.Time.Utc.AddSeconds(60), h.Scheduler.NextWakeUtc);
    }

    [Fact]
    public void StartingAPlaylistStopsAnAutomation_AndTheOtherWayRound()
    {
        var h = new Harness();
        var automation = Timer("t", ("a", 60));
        var playlist = Playlist("p", 5, false, "x", "y");
        h.Scheduler.Update([automation], [playlist]);

        h.Scheduler.StartAutomation(automation);
        Assert.Equal("t", h.Scheduler.ActiveAutomationID);
        h.Scheduler.StartPlaylist(playlist);
        Assert.Null(h.Scheduler.ActiveAutomationID);
        Assert.Equal("p", h.Scheduler.ActivePlaylistID);
        h.Scheduler.StartAutomation(automation);
        Assert.Null(h.Scheduler.ActivePlaylistID);
    }

    // ---- Clock automations

    [Fact]
    public void ClockAutomation_ShowsTheWindowForTheTimeOfDay_AndWakesAtTheNextBoundary()
    {
        var h = new Harness();
        var day = ClockDay("c", ("morning", 420, 1200), ("evening", 1200, 1260));
        h.Scheduler.Update([day], []);
        h.Time.SetLocal(9, 30, 15);
        h.Scheduler.StartAutomation(day);

        Assert.Equal("morning", h.Current);
        // The next edge is 20:00, 10 h 30 min away, less the 15 seconds already into this minute.
        Assert.Equal(h.Time.Utc.AddSeconds(10 * 3600 + 30 * 60 - 15), h.Scheduler.NextWakeUtc);

        h.Time.Advance(TimeSpan.FromSeconds(10 * 3600 + 30 * 60 - 15));
        h.Scheduler.Fire();
        Assert.Equal("evening", h.Current);
    }

    /// A gap keeps whatever is playing: the documented rule.
    [Fact]
    public void ClockAutomation_InsideAGap_LeavesTheScreenAlone()
    {
        var h = new Harness { Current = "something" };
        var day = ClockDay("c", ("morning", 420, 1200));
        h.Scheduler.Update([day], []);
        h.Time.SetLocal(23, 0);
        h.Scheduler.StartAutomation(day);
        Assert.Empty(h.Applied);
        Assert.Equal("something", h.Current);
        Assert.NotNull(h.Scheduler.NextWakeUtc);   // still wakes for tomorrow morning
    }

    [Fact]
    public void ClockAutomation_ARestartedEngineShowsWhatIsDueNow()
    {
        var h = new Harness();
        var day = ClockDay("c", ("night", 1320, 360), ("day", 360, 1320));
        h.Scheduler.Update([day], []);
        h.Time.SetLocal(2, 0);
        h.Scheduler.Restore("c", null, 0, h.Time.Utc);
        h.Scheduler.Reevaluate();
        Assert.Equal("night", h.Current);   // 02:00 is inside the window that wraps midnight
    }

    // ---- Edits and deletions

    [Fact]
    public void Update_StopsAScheduleThatWasEmptiedOrDeleted()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, false, "a", "b");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);

        h.Scheduler.Update([], [playlist with { WallpaperIDs = [] }]);
        Assert.False(h.Scheduler.IsRunning);

        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        h.Scheduler.Update([], []);                // deleted outright
        Assert.False(h.Scheduler.IsRunning);
    }

    [Fact]
    public void Update_KeepsARunningScheduleThatStillHasWallpapers()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, false, "a", "b");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        h.Scheduler.Update([], [playlist with { WallpaperIDs = ["b"] }]);
        Assert.True(h.Scheduler.IsRunning);
    }

    [Fact]
    public void ShowCurrentAgain_MovesAPlaylistOn_AndFillsAClockGapWithTheLastWindow()
    {
        var h = new Harness();
        var playlist = Playlist("p", 5, false, "a", "b");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);   // showing a
        h.Current = null;                      // a was deleted and the desktop cleared
        h.Scheduler.ShowCurrentAgain();
        Assert.Equal("a", h.Current);          // not in the list as shown: starts from the first

        var day = ClockDay("c", ("morning", 420, 1200), ("evening", 1200, 1260));
        var h2 = new Harness();
        h2.Scheduler.Update([day], []);
        h2.Time.SetLocal(23, 30);
        h2.Scheduler.StartAutomation(day);     // a gap: nothing applied
        Assert.Null(h2.Current);
        h2.Scheduler.ShowCurrentAgain();       // what was playing is gone: the last window answers
        Assert.Equal("evening", h2.Current);
    }

    [Fact]
    public void ARunningScheduleReportsItsName_AndNothingRunningHasNoWake()
    {
        var h = new Harness();
        Assert.Null(h.Scheduler.NextWakeUtc);
        Assert.Null(h.Scheduler.RunningName);
        var playlist = Playlist("p", 5, false, "a");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        Assert.Equal("p", h.Scheduler.RunningName);
    }

    [Fact]
    public void TheWakeIsNeverInThePast()
    {
        var h = new Harness();
        var playlist = Playlist("p", 1, false, "a", "b");
        h.Scheduler.Update([], [playlist]);
        h.Scheduler.StartPlaylist(playlist);
        h.Time.Advance(TimeSpan.FromHours(5));                         // long past the deadline
        Assert.Equal(h.Time.Utc.AddSeconds(1), h.Scheduler.NextWakeUtc);
    }
}
