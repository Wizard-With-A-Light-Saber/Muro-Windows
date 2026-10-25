using System.Text.Json.Serialization;

namespace Muro.Core.Schedule;

public enum AutomationMode
{
    /// Wallpaper 1 for 10 seconds, wallpaper 2 for 20 minutes, then back to the top.
    Timer,
    /// Wallpaper 1 from 07:00 to 20:00, wallpaper 2 from 20:00 to 21:00, repeating daily. May wrap midnight.
    Clock,
}

public enum ClockEdge { Start, End }

/// One wallpaper place in a schedule. Seconds is used in timer mode, the two minute marks in clock mode;
/// the unused pair stays null so switching mode back and forth never loses what was typed.
public sealed record Step
{
    public string Id { get; init; } = Guid.NewGuid().ToString().ToLowerInvariant();
    public string WallpaperID { get; init; } = "";
    public int? Seconds { get; init; }
    public int? StartMinute { get; init; }
    public int? EndMinute { get; init; }

    /// Clamped to something the scheduler can run: 10 seconds is the shortest useful step, 24 hours
    /// the longest meaningful one. An unset step is ten minutes.
    [JsonIgnore] public int Duration => Math.Clamp(Seconds ?? 600, 10, 86_400);
    [JsonIgnore] public int Start => Automation.NormalizedMinute(StartMinute ?? 0);
    [JsonIgnore] public int End => Automation.NormalizedMinute(EndMinute ?? 0);

    /// Length of a clock window in minutes, counting a wrap past midnight (22:00 to 06:00 is 480).
    [JsonIgnore] public int ClockLength { get { var span = End - Start; return span > 0 ? span : span + 1440; } }

    /// Whether a minute of the day falls inside this window. The same start and end means the whole
    /// day, not nothing: a zero-length window is useless, and equal ends is the only way the editor
    /// can say "this one, always".
    public bool Covers(int minute)
    {
        var now = Automation.NormalizedMinute(minute);
        if (Start == End) return true;
        return Start < End ? now >= Start && now < End : now >= Start || now < End;
    }
}

/// A schedule of wallpapers. Where a playlist rotates on one fixed interval, an automation gives
/// every wallpaper its own duration, or its own slot on the clock.
public sealed record Automation
{
    public string Id { get; init; } = Guid.NewGuid().ToString().ToLowerInvariant();
    public string Name { get; init; } = "";
    public AutomationMode Mode { get; init; } = AutomationMode.Timer;
    public List<Step> Steps { get; init; } = [];
    public bool Enabled { get; init; } = true;

    /// How long one full pass through a timer automation takes.
    [JsonIgnore] public int CycleSeconds => Steps.Sum(s => s.Duration);

    /// Minutes of the day wrapped into 0 up to 1440.
    public static int NormalizedMinute(int minute)
    {
        var wrapped = minute % 1440;
        return wrapped < 0 ? wrapped + 1440 : wrapped;
    }

    public static bool IsAllDay(Step step) => step.Start == step.End;

    /// Gaps in a clock schedule as (start, end) minute pairs. A gap is not an error: whatever was
    /// playing simply stays. The editor shows them so a loose schedule is a choice, not a surprise.
    public List<(int Start, int End)> UncoveredWindows()
    {
        if (Mode != AutomationMode.Clock || Steps.Count == 0) return [];
        var covered = new bool[1440];
        foreach (var step in Steps)
            for (var offset = 0; offset < step.ClockLength; offset++) covered[(step.Start + offset) % 1440] = true;

        var gaps = new List<(int Start, int End)>();
        var minute = 0;
        while (minute < 1440)
        {
            if (covered[minute]) { minute++; continue; }
            var start = minute;
            while (minute < 1440 && !covered[minute]) minute++;
            gaps.Add((start, minute % 1440));
        }
        // A gap that runs over midnight is one gap: "21:00 to 07:00", not "00:00 to 07:00" and "21:00 to 00:00".
        if (gaps.Count > 1 && gaps[0].Start == 0 && gaps[^1].End == 0)
        {
            var tail = gaps[^1];
            gaps.RemoveAt(gaps.Count - 1);
            gaps[0] = (tail.Start, gaps[0].End);
        }
        return gaps;
    }

    /// Minutes until the next moment the answer could change: the nearest window edge ahead. The
    /// scheduler sets one timer from this, so a whole day costs one wake rather than one per window.
    /// Never zero, so a boundary that has just passed cannot spin the timer.
    public int MinutesToNextBoundary(int minute)
    {
        var best = 1440;
        foreach (var step in Steps.Where(s => s.Start != s.End))
            foreach (var boundary in new[] { step.Start, step.End })
            {
                var delta = boundary - NormalizedMinute(minute);
                best = Math.Min(best, delta > 0 ? delta : delta + 1440);
            }
        return Math.Max(1, best);
    }

    /// The step that should be showing at a minute of the day. First match wins on overlap.
    public Step? ClockStep(int minute) => Steps.FirstOrDefault(s => s.Covers(minute));

    /// ClockStep, or inside a gap the step whose window ended last. For one case only: what was
    /// playing has been deleted, so a gap has nothing to keep. Ordinary ticks use ClockStep, so a gap
    /// still leaves the screen alone.
    public Step? ClockStepOrLast(int minute)
    {
        if (ClockStep(minute) is { } step) return step;
        var now = NormalizedMinute(minute);
        return Steps.MinBy(s => NormalizedMinute(now - s.End));
    }

    // ---- Editing a clock schedule

    /// Lays a clock schedule end to end across the day, in list order, in equal slices. A count that
    /// does not divide 1440 evenly leaves its remainder on the last window rather than spreading a
    /// rounding error. One wallpaper is stored as the same start and end, which reads as all day.
    public static List<Step> FittedToDay(IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) return [];
        if (steps.Count == 1) return [steps[0] with { StartMinute = 0, EndMinute = 0 }];
        var slice = 1440 / steps.Count;
        return steps.Select((step, index) => step with
        {
            StartMinute = index * slice,
            EndMinute = index == steps.Count - 1 ? 0 : (index + 1) * slice,
        }).ToList();
    }

    /// Where a wallpaper goes when it is added. If the day still has room it takes the largest free
    /// run and nothing already arranged moves; if the day is full the only way is to divide it again.
    public static List<Step> FittedAdding(Step step, IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0) return FittedToDay([step]);
        var existing = new Automation { Mode = AutomationMode.Clock, Steps = steps.ToList() };
        var gaps = existing.UncoveredWindows()
            .Select(gap => (gap.Start, Length: gap.End > gap.Start ? gap.End - gap.Start : gap.End + 1440 - gap.Start))
            .OrderByDescending(g => g.Length)
            .ToList();
        if (gaps.Count == 0 || gaps[0].Length <= 0) return FittedToDay(steps.Append(step).ToList());
        var w = gaps[0];
        var placed = step with { StartMinute = NormalizedMinute(w.Start), EndMinute = NormalizedMinute(w.Start + w.Length) };
        return [.. steps, placed];
    }

    /// Moving a wallpaper up or down the list. In clock mode a slot belongs to the place in the list,
    /// not to the wallpaper sitting in it, so with carryingSlots the moved wallpaper takes the hours of
    /// the row it was dropped on. In timer mode each wallpaper owns its length, so only the order changes.
    public static List<Step> Reordered(IReadOnlyList<Step> steps, int from, int to, bool carryingSlots)
    {
        if (from < 0 || from >= steps.Count || to < 0 || to >= steps.Count || from == to) return steps.ToList();
        var slots = steps.Select(s => (s.StartMinute, s.EndMinute)).ToList();
        var moved = steps.ToList();
        var item = moved[from];
        moved.RemoveAt(from);
        moved.Insert(to, item);
        if (!carryingSlots) return moved;
        for (var i = 0; i < moved.Count; i++) moved[i] = moved[i] with { StartMinute = slots[i].StartMinute, EndMinute = slots[i].EndMinute };
        return moved;
    }

    /// A clock window with one edge moved by delta minutes, by the rules the day timeline drags it
    /// with. An edge shared with the next window takes that window edge along, a free edge moves only
    /// through minutes no other window owns, and no window gets shorter than the minimum. A whole-day
    /// window has no edge to move.
    public static List<Step> MovingEdge(ClockEdge edge, string stepId, int delta, IReadOnlyList<Step> steps, int minimumLength = 15)
    {
        var index = steps.ToList().FindIndex(s => s.Id == stepId);
        if (index < 0) return steps.ToList();
        var step = steps[index];
        if (IsAllDay(step)) return steps.ToList();

        var length = step.ClockLength;
        var busy = new bool[1440];
        foreach (var other in steps.Where(o => o.Id != stepId))
            for (var offset = 0; offset < other.ClockLength; offset++) busy[(other.Start + offset) % 1440] = true;

        int FreeRun(int minute, int direction)
        {
            var count = 0;
            var probe = NormalizedMinute(direction < 0 ? minute - 1 : minute);
            while (count < 1440 - length && !busy[probe])
            {
                count++;
                probe = NormalizedMinute(probe + direction);
            }
            return count;
        }

        int joined;
        int lowest, highest;
        if (edge == ClockEdge.Start)
        {
            joined = steps.ToList().FindIndex(o => o.Id != stepId && !IsAllDay(o) && o.End == step.Start);
            lowest = joined >= 0 ? -(steps[joined].ClockLength - minimumLength) : -FreeRun(step.Start, -1);
            highest = length - minimumLength;
        }
        else
        {
            joined = steps.ToList().FindIndex(o => o.Id != stepId && !IsAllDay(o) && o.Start == step.End);
            highest = joined >= 0 ? steps[joined].ClockLength - minimumLength : FreeRun(step.End, 1);
            lowest = -(length - minimumLength);
        }
        var applied = Math.Min(Math.Max(delta, lowest), highest);
        if (applied == 0) return steps.ToList();

        var result = steps.ToList();
        if (edge == ClockEdge.Start)
        {
            var start = NormalizedMinute(step.Start + applied);
            result[index] = step with { StartMinute = start };
            if (joined >= 0) result[joined] = result[joined] with { EndMinute = start };
        }
        else
        {
            var end = NormalizedMinute(step.End + applied);
            result[index] = step with { EndMinute = end };
            if (joined >= 0) result[joined] = result[joined] with { StartMinute = end };
        }
        return result;
    }

    /// A clock schedule after one wallpaper is taken out. The freed minutes go to the window that ended
    /// where the removed one started, so the day stays covered and nothing else moves. With one left
    /// there is nothing to arrange, so it takes the whole day.
    public static List<Step> FittedRemoving(Step step, IReadOnlyList<Step> steps)
    {
        var rest = steps.Where(s => s.Id != step.Id).ToList();
        if (rest.Count <= 1) return FittedToDay(rest);
        // A whole-day window overlapped everything, so there are no minutes to hand back.
        if (IsAllDay(step)) return rest;
        var index = rest.FindIndex(s => !IsAllDay(s) && s.End == step.Start);
        if (index >= 0) rest[index] = rest[index] with { EndMinute = step.End };
        return rest;
    }

    // ---- Labels

    /// "07:00", "20:30" in the user own clock format.
    public static string ClockLabel(int minute)
    {
        var m = NormalizedMinute(minute);
        return DateTime.Today.AddMinutes(m).ToString("t");
    }

    /// "10 s", "5 min", "2 h", "1 h 30 min".
    public static string DurationLabel(int seconds)
    {
        if (seconds < 60) return seconds + " s";
        if (seconds < 3600)
        {
            var minutes = seconds / 60;
            var rest = seconds % 60;
            return rest == 0 ? minutes + " min" : minutes + " min " + rest + " s";
        }
        var hours = seconds / 3600;
        var mins = seconds % 3600 / 60;
        return mins == 0 ? hours + " h" : hours + " h " + mins + " min";
    }
}

public static class AutomationStore
{
    public static string PathIn(string root) => Path.Combine(root, "automations.json");

    public static List<Automation> Load(string root) => ScheduleJson.LoadList<Automation>(PathIn(root));

    public static void Save(string root, IEnumerable<Automation> automations) => ScheduleJson.Save(PathIn(root), automations.ToList());

    /// A deleted wallpaper must not be left behind as a step pointing at a file that is gone. In clock
    /// mode the freed minutes go to the window before it, so the day stays covered; in timer mode the
    /// step simply goes.
    public static (List<Automation> Automations, List<string> Emptied) Pruned(IEnumerable<Automation> automations, ISet<string> removing)
    {
        var emptied = new List<string>();
        var result = automations.Select(automation =>
        {
            var steps = automation.Steps;
            if (automation.Mode == AutomationMode.Clock)
            {
                foreach (var step in automation.Steps.Where(s => removing.Contains(s.WallpaperID)))
                    steps = Automation.FittedRemoving(step, steps);
            }
            else
            {
                steps = steps.Where(s => !removing.Contains(s.WallpaperID)).ToList();
            }
            if (steps.Count == 0 && automation.Steps.Count > 0) emptied.Add(automation.Id);
            return steps.Count == automation.Steps.Count && steps.SequenceEqual(automation.Steps) ? automation : automation with { Steps = steps };
        }).ToList();
        return (result, emptied);
    }
}
