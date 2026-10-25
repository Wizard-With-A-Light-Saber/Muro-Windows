using Muro.Core.Schedule;

namespace Muro.Core.Tests;

/// Ported from the macOS AutomationTests: they encode the edge cases (midnight wrap, all-day
/// windows, fitting, edge dragging) that the timeline editor depends on.
public class AutomationTests
{
    private static Automation Clock(params (string Id, int Start, int End)[] windows) => new()
    {
        Name = "Day",
        Mode = AutomationMode.Clock,
        Steps = windows.Select(w => new Step { WallpaperID = w.Id, StartMinute = w.Start, EndMinute = w.End }).ToList(),
    };

    private static Step S(string id, int? start = null, int? end = null) => new() { WallpaperID = id, StartMinute = start, EndMinute = end };

    private static List<(int Start, int End)> Gaps(IEnumerable<Step> steps) =>
        new Automation { Mode = AutomationMode.Clock, Steps = steps.ToList() }.UncoveredWindows();

    // ---- Storage

    [Fact]
    public void Automations_RoundTripThroughTheFile_InTheMacShape()
    {
        using var lib = new TempLibrary();
        var automations = new List<Automation>
        {
            new() { Name = "Morning", Mode = AutomationMode.Timer, Steps = [new() { WallpaperID = "a", Seconds = 10 }, new() { WallpaperID = "b", Seconds = 7200 }] },
            Clock(("c", 420, 480)),
        };
        AutomationStore.Save(lib.Root, automations);
        var text = File.ReadAllText(AutomationStore.PathIn(lib.Root));
        Assert.Contains("\"mode\": \"timer\"", text);
        Assert.Contains("\"mode\": \"clock\"", text);
        Assert.Contains("\"wallpaperID\": \"a\"", text);

        var loaded = AutomationStore.Load(lib.Root);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(AutomationMode.Timer, loaded[0].Mode);
        Assert.Equal(new int?[] { 10, 7200 }, loaded[0].Steps.Select(s => s.Seconds).ToArray());
        Assert.Equal(AutomationMode.Clock, loaded[1].Mode);
        Assert.Equal(420, loaded[1].Steps[0].StartMinute);
    }

    [Fact]
    public void MissingOrDamagedAutomationsFile_LoadsAsEmpty()
    {
        using var lib = new TempLibrary();
        Assert.Empty(AutomationStore.Load(lib.Root));
        File.WriteAllText(AutomationStore.PathIn(lib.Root), "{ not json");
        Assert.Empty(AutomationStore.Load(lib.Root));
    }

    // ---- Timer mode

    [Fact]
    public void CycleLength_IsTheSumOfTheSteps()
    {
        var automation = new Automation
        {
            Steps = [new() { WallpaperID = "a", Seconds = 10 }, new() { WallpaperID = "b", Seconds = 1200 }, new() { WallpaperID = "c", Seconds = 7200 }],
        };
        Assert.Equal(8410, automation.CycleSeconds);
        Assert.Equal("2 h 20 min", Automation.DurationLabel(8410));
    }

    /// A step with nothing set, or something absurd, must not make the scheduler fire in a tight loop or never.
    [Fact]
    public void Durations_AreClampedToSomethingRunnable()
    {
        Assert.Equal(10, new Step { Seconds = 0 }.Duration);
        Assert.Equal(10, new Step { Seconds = -5 }.Duration);
        Assert.Equal(600, new Step().Duration);
        Assert.Equal(86_400, new Step { Seconds = 999_999 }.Duration);
    }

    // ---- Clock mode

    [Fact]
    public void TheRightWallpaperIsPickedForATimeOfDay()
    {
        var automation = Clock(("morning", 420, 1200), ("evening", 1200, 1260));
        Assert.Equal("morning", automation.ClockStep(420)?.WallpaperID);
        Assert.Equal("morning", automation.ClockStep(1199)?.WallpaperID);
        Assert.Equal("evening", automation.ClockStep(1200)?.WallpaperID);
        Assert.Equal("evening", automation.ClockStep(1259)?.WallpaperID);
        // 21:00 to 07:00 is a gap: nothing matches, so what plays keeps playing.
        Assert.Null(automation.ClockStep(1260));
        Assert.Null(automation.ClockStep(60));
    }

    [Fact]
    public void AWindowCanRunPastMidnight()
    {
        var night = new Step { WallpaperID = "night", StartMinute = 1320, EndMinute = 360 };
        Assert.Equal(480, night.ClockLength);
        Assert.True(night.Covers(1380));
        Assert.True(night.Covers(0));
        Assert.True(night.Covers(359));
        Assert.False(night.Covers(360));
        Assert.False(night.Covers(720));
    }

    [Fact]
    public void OverlapIsResolvedByOrder() =>
        Assert.Equal("first", Clock(("first", 0, 1440), ("second", 600, 700)).ClockStep(650)?.WallpaperID);

    [Fact]
    public void AGapOverMidnightIsReportedAsOne()
    {
        var gaps = Clock(("morning", 420, 1200), ("evening", 1200, 1260)).UncoveredWindows();
        Assert.Single(gaps);
        Assert.Equal((1260, 420), gaps[0]);
        Assert.Empty(Clock(("all", 0, 1440)).UncoveredWindows());
    }

    [Fact]
    public void TheSameStartAndEndMeansAllDay()
    {
        var always = new Step { WallpaperID = "a", StartMinute = 540, EndMinute = 540 };
        Assert.Equal(1440, always.ClockLength);
        Assert.True(always.Covers(0));
        Assert.True(always.Covers(539));
        Assert.True(always.Covers(1439));
    }

    /// The scheduler sets one timer for a whole day, so the next boundary has to be right.
    [Fact]
    public void TheNextBoundaryIsTheNearestEdgeAhead()
    {
        var automation = Clock(("morning", 420, 1200), ("evening", 1200, 1260));
        Assert.Equal(20, automation.MinutesToNextBoundary(400));
        Assert.Equal(780, automation.MinutesToNextBoundary(420));
        Assert.Equal(50, automation.MinutesToNextBoundary(1210));
        Assert.Equal(560, automation.MinutesToNextBoundary(1300));   // past the last window: tomorrow morning
    }

    [Fact]
    public void TheLastWindowAnswersInsideAGap()
    {
        var automation = Clock(("morning", 420, 1200), ("evening", 1200, 1260));
        Assert.Null(automation.ClockStep(1300));
        Assert.Equal("evening", automation.ClockStepOrLast(1300)?.WallpaperID);
        Assert.Equal("evening", automation.ClockStepOrLast(60)?.WallpaperID);    // past midnight
        Assert.Equal("morning", automation.ClockStepOrLast(600)?.WallpaperID);   // not a gap
    }

    // ---- Deleted wallpapers

    [Fact]
    public void DeletedWallpapersLeaveNoStepsBehind()
    {
        var automations = new List<Automation>
        {
            new() { Name = "Mixed", Steps = [new() { WallpaperID = "a", Seconds = 10 }, new() { WallpaperID = "b", Seconds = 10 }] },
            new() { Name = "Doomed", Steps = [new() { WallpaperID = "a", Seconds = 10 }] },
        };
        var (result, emptied) = AutomationStore.Pruned(automations, new HashSet<string> { "a" });
        Assert.Equal(["b"], result[0].Steps.Select(s => s.WallpaperID));
        Assert.Empty(result[1].Steps);
        Assert.Single(emptied);
    }

    /// A deleted wallpaper hours go to the window before it, so the day stays covered.
    [Fact]
    public void ADeletedClockWallpaperLeavesNoGap()
    {
        var day = Clock(("a", 0, 480), ("b", 480, 960), ("c", 960, 0));
        var (result, emptied) = AutomationStore.Pruned([day], new HashSet<string> { "b" });
        var pruned = result[0];
        Assert.Equal(["a", "c"], pruned.Steps.Select(s => s.WallpaperID));
        Assert.Empty(pruned.UncoveredWindows());
        Assert.Equal("a", pruned.ClockStep(600)?.WallpaperID);
        Assert.Empty(emptied);
    }

    [Fact]
    public void DeletingDownToOneClockWallpaperCoversTheDay()
    {
        var day = Clock(("a", 0, 480), ("b", 480, 960), ("c", 960, 0));
        var pruned = AutomationStore.Pruned([day], new HashSet<string> { "a", "b" }).Automations[0];
        Assert.Equal(["c"], pruned.Steps.Select(s => s.WallpaperID));
        Assert.Equal("c", pruned.ClockStep(100)?.WallpaperID);
        Assert.Equal("c", pruned.ClockStep(1000)?.WallpaperID);
    }

    // ---- Moving an edge

    [Fact]
    public void AnEdgeSharedWithTheNextWindowMovesBoth()
    {
        var steps = Automation.FittedToDay([S("pixelwave"), S("purple"), S("monterey")]);
        var id = steps[0].Id;
        steps = Automation.MovingEdge(ClockEdge.End, id, 15, steps);
        Assert.Equal(495, steps[0].End);
        Assert.Equal(495, steps[1].Start);     // the neighbour start moves with it
        for (var i = 0; i < 60; i++) steps = Automation.MovingEdge(ClockEdge.End, id, 15, steps);
        Assert.Equal(945, steps[0].End);       // stops 15 minutes short of the neighbour end
        Assert.Equal(945, steps[1].Start);
        Assert.Equal(960, steps[1].End);
        Assert.Empty(Gaps(steps));
        for (var minute = 0; minute < 1440; minute += 5)
            Assert.Equal(1, steps.Count(s => s.Covers(minute)));
    }

    [Fact]
    public void AFreeEdgeStopsAtTheNextWindow()
    {
        var steps = Clock(("a", 480, 600), ("b", 720, 900)).Steps;
        steps = Automation.MovingEdge(ClockEdge.End, steps[0].Id, 300, steps);
        Assert.Equal(720, steps[0].End);       // up to b and no further
        Assert.Equal(720, steps[1].Start);     // b is untouched
        steps = Automation.MovingEdge(ClockEdge.Start, steps[0].Id, -1000, steps);
        Assert.Equal(920, steps[0].Start);     // back round midnight, into the free run
        steps = Automation.MovingEdge(ClockEdge.Start, steps[0].Id, -100, steps);
        Assert.Equal(900, steps[0].Start);     // and no further than b end
    }

    [Fact]
    public void AWindowKeepsAQuarterOfAnHour()
    {
        var steps = Clock(("a", 480, 600), ("b", 600, 720)).Steps;
        steps = Automation.MovingEdge(ClockEdge.End, steps[0].Id, -500, steps);
        Assert.Equal(495, steps[0].End);
        Assert.Equal(495, steps[1].Start);     // the shared edge still moves b
    }

    [Fact]
    public void AWholeDayWindowDoesNotMove()
    {
        var steps = Automation.FittedToDay([S("a")]);
        Assert.Equal(steps, Automation.MovingEdge(ClockEdge.End, steps[0].Id, 15, steps));
    }

    // ---- Labels

    [Fact]
    public void DurationLabels()
    {
        Assert.Equal("10 s", Automation.DurationLabel(10));
        Assert.Equal("1 min", Automation.DurationLabel(60));
        Assert.Equal("1 min 30 s", Automation.DurationLabel(90));
        Assert.Equal("1 h", Automation.DurationLabel(3600));
        Assert.Equal("2 h 30 min", Automation.DurationLabel(9000));
    }

    [Fact]
    public void MinutesWrapInsteadOfGoingNegative()
    {
        Assert.Equal(1425, Automation.NormalizedMinute(-15));
        Assert.Equal(0, Automation.NormalizedMinute(1440));
        Assert.Equal(60, Automation.NormalizedMinute(1500));
    }

    // ---- Fitting a clock schedule to the day

    private static List<Step> Steps(int count) => Enumerable.Range(0, count).Select(i => S("w" + i)).ToList();

    [Fact]
    public void OneWallpaperCoversTheWholeDay()
    {
        var fitted = Automation.FittedToDay(Steps(1));
        Assert.Single(fitted);
        Assert.True(Automation.IsAllDay(fitted[0]));
        Assert.Empty(Gaps(fitted));
    }

    [Fact]
    public void TwoSplitTheDayInHalf_ThreeTakeEightHoursEach()
    {
        var two = Automation.FittedToDay(Steps(2));
        Assert.Equal((0, 720, 720, 0), (two[0].Start, two[0].End, two[1].Start, two[1].End));
        Assert.Equal(new[] { 0, 480, 960 }, Automation.FittedToDay(Steps(3)).Select(s => s.Start).ToArray());
    }

    /// Counts that do not divide 1440 evenly are the ones that would leave a hole if the remainder were dropped.
    [Fact]
    public void AnyCountStillCoversTheWholeDay_WithNothingOverlapping()
    {
        for (var count = 1; count <= 12; count++)
        {
            var fitted = Automation.FittedToDay(Steps(count));
            Assert.Empty(Gaps(fitted));
            Assert.Equal(count, fitted.Select(s => s.WallpaperID).Distinct().Count());
            var owner = new string?[1440];
            foreach (var step in fitted)
            {
                var length = Automation.IsAllDay(step) ? 1440 : step.ClockLength;
                for (var offset = 0; offset < length; offset++)
                {
                    var minute = (step.Start + offset) % 1440;
                    Assert.True(owner[minute] is null, count + " wallpapers overlap at minute " + minute);
                    owner[minute] = step.WallpaperID;
                }
            }
        }
    }

    [Fact]
    public void FirstWallpaperTakesTheWholeDay_AndAddingToAFullDaySplitsIt()
    {
        var one = Automation.FittedAdding(S("a"), []);
        Assert.True(Automation.IsAllDay(one.Single()));
        var two = Automation.FittedAdding(S("b"), one);
        Assert.Equal(new[] { 0, 720 }, two.Select(s => s.Start).ToArray());
        var three = Automation.FittedAdding(S("c"), two);
        Assert.Equal(new[] { 0, 480, 960 }, three.Select(s => s.Start).ToArray());
        Assert.Empty(Gaps(three));
    }

    /// 00:00-03:00 and 03:00-09:00 arranged by hand, 09:00-24:00 empty: a third wallpaper belongs in the
    /// empty part and the first two must not move.
    [Fact]
    public void AddingFillsTheGapAndLeavesTheArrangementAlone()
    {
        var result = Automation.FittedAdding(S("c"), [S("a", 0, 180), S("b", 180, 540)]);
        Assert.Equal(new[] { (0, 180), (180, 540), (540, 0) }, result.Select(s => (s.Start, s.End)).ToArray());
        Assert.Empty(Gaps(result));
    }

    [Fact]
    public void AddingTakesTheLargestGapWhenThereAreSeveral()
    {
        // Free: 02:00-05:00 (180) and 08:00-20:00 (720).
        var result = Automation.FittedAdding(S("d"), [S("a", 0, 120), S("b", 300, 480), S("c", 1200, 0)]);
        Assert.Equal((480, 1200), (result[^1].Start, result[^1].End));
    }

    [Fact]
    public void AddingAcrossAMidnightGap()
    {
        var result = Automation.FittedAdding(S("b"), [S("a", 360, 540)]);
        Assert.Equal((540, 360), (result[1].Start, result[1].End));
        Assert.Empty(Gaps(result));
    }

    [Fact]
    public void RemovingLeavesTheOthersWhereTheyWere()
    {
        var arranged = new List<Step> { S("a", 0, 180), S("b", 180, 540), S("c", 540, 0) };
        var result = Automation.FittedRemoving(arranged[1], arranged);
        Assert.Equal(["a", "c"], result.Select(s => s.WallpaperID));
        Assert.Equal((0, 540), (result[0].Start, result[0].End));
        Assert.Equal(540, result[1].Start);
        Assert.Empty(Gaps(result));
    }

    [Fact]
    public void RemovingDownToOneGivesItTheWholeDay()
    {
        var arranged = new List<Step> { S("a", 0, 720), S("b", 720, 0) };
        Assert.True(Automation.IsAllDay(Automation.FittedRemoving(arranged[1], arranged).Single()));
    }

    /// The hours belong to the rows, so the wallpapers swap slots and the day is drawn the other way round.
    [Fact]
    public void ReorderingCarriesTheSlotsNotTheTimes()
    {
        var arranged = new List<Step> { S("a", 0, 720), S("b", 720, 0) };
        var result = Automation.Reordered(arranged, 1, 0, carryingSlots: true);
        Assert.Equal(["b", "a"], result.Select(s => s.WallpaperID));
        Assert.Equal((0, 720, 720, 0), (result[0].Start, result[0].End, result[1].Start, result[1].End));

        var three = new List<Step> { S("a", 0, 180), S("b", 180, 540), S("c", 540, 0) };
        var moved = Automation.Reordered(three, 2, 0, carryingSlots: true);
        Assert.Equal(["c", "a", "b"], moved.Select(s => s.WallpaperID));
        Assert.Equal(new[] { 0, 180, 540 }, moved.Select(s => s.Start).ToArray());
        Assert.Empty(Gaps(moved));
    }

    [Fact]
    public void ReorderingWithoutSlots_KeepsEachWallpapersOwnTimes_AndOutOfRangeChangesNothing()
    {
        var arranged = new List<Step> { S("a", 0, 720), S("b", 720, 0) };
        var result = Automation.Reordered(arranged, 1, 0, carryingSlots: false);
        Assert.Equal(["b", "a"], result.Select(s => s.WallpaperID));
        Assert.Equal(new[] { 720, 0 }, result.Select(s => s.Start).ToArray());
        Assert.Equal(["a", "b"], Automation.Reordered(arranged, 0, 0, true).Select(s => s.WallpaperID));
        Assert.Equal(["a", "b"], Automation.Reordered(arranged, 5, 0, true).Select(s => s.WallpaperID));
    }

    [Fact]
    public void AddingThenRemovingReturnsTheDayToOneWallpaper()
    {
        var steps = Automation.FittedAdding(S("a"), []);
        steps = Automation.FittedAdding(S("b"), steps);
        steps = Automation.FittedRemoving(steps[1], steps);
        Assert.Equal(["a"], steps.Select(s => s.WallpaperID));
        Assert.True(Automation.IsAllDay(steps[0]));
    }
}
