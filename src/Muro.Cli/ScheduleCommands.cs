using System.Text.RegularExpressions;
using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;

namespace Muro.Cli;

/// playlist, automation, schedule and pause-after commands. They write intent (playlists.json,
/// automations.json, schedule.json, config.json); the engine carries it out.
internal static partial class ScheduleCommands
{
    public const string Help = """
        muro playlist list | new <name> <wallpaper>... [--minutes N] [--shuffle] | play <name> | stop | delete <name>
        muro automation list | new-timer <name> <wallpaper>=<seconds>... | new-clock <name> <wallpaper>=HH:MM-HH:MM...
                        | play <name> | stop | delete <name>
        muro schedule                                          what is running now
        muro pause-after <30s|5m|1h|off> [--wallpaper <name>]  freeze a wallpaper on a frame after a while
        muro pause-after default --wallpaper <name>            follow the global setting again
        """;

    public static int Run(string[] args, string root)
    {
        var rest = args.Skip(1).ToList();
        return args[0] switch
        {
            "playlist" => Playlists(rest, root),
            "automation" => Automations(rest, root),
            "schedule" => Status(root),
            "pause-after" => PauseAfterCommand(rest, root),
            _ => Fail("Unknown command."),
        };
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    // ---- Playlists

    private static int Playlists(List<string> rest, string root)
    {
        var playlists = PlaylistStore.Load(root);
        switch (rest.FirstOrDefault())
        {
            case "list" or null:
                foreach (var p in playlists)
                    Console.WriteLine(p.Name.PadRight(28) + p.WallpaperIDs.Count + " wallpapers, every " + p.IntervalMinutes + " min" + (p.Shuffle ? ", shuffled" : ", in order"));
                Console.WriteLine(playlists.Count + " playlists.");
                return 0;
            case "new":
            {
                var words = Positional(rest.Skip(1).ToList(), "--minutes");
                if (words.Count < 2) return Fail("Usage: muro playlist new <name> <wallpaper>... [--minutes N] [--shuffle]");
                var ids = new List<string>();
                foreach (var selector in words.Skip(1))
                {
                    if (Pick(selector, root) is not { } entry) return 1;
                    ids.Add(entry.Id);
                }
                var minutes = int.TryParse(Option(rest, "--minutes"), out var m) ? Math.Max(1, m) : 30;
                var name = Names.Unique(words[0], playlists.Select(p => p.Name));
                playlists.Add(new Playlist { Name = name, WallpaperIDs = ids, IntervalMinutes = minutes, Shuffle = rest.Contains("--shuffle") });
                PlaylistStore.Save(root, playlists);
                Console.WriteLine("Created playlist " + name + " with " + ids.Count + " wallpapers.");
                return 0;
            }
            case "play":
            {
                if (Find(playlists, rest, p => p.Name) is not { } playlist) return 1;
                if (playlist.WallpaperIDs.Count == 0) return Fail(playlist.Name + " is empty.");
                ScheduleControl.StartPlaylist(root, Surface.Desktop, playlist.Id);
                Console.WriteLine("Starting " + playlist.Name + ". The engine takes it from here.");
                return 0;
            }
            case "stop":
                ScheduleControl.Stop(root, Surface.Desktop);
                Console.WriteLine("Stopped.");
                return 0;
            case "delete":
            {
                if (Find(playlists, rest, p => p.Name) is not { } playlist) return 1;
                if (ScheduleStore.Load(root, Surface.Desktop).PlaylistID == playlist.Id) ScheduleControl.Stop(root, Surface.Desktop, restore: false);
                PlaylistStore.Save(root, playlists.Where(p => p.Id != playlist.Id));
                Console.WriteLine("Deleted " + playlist.Name + ".");
                return 0;
            }
        }
        return Fail(Help);
    }

    // ---- Automations

    private static int Automations(List<string> rest, string root)
    {
        var automations = AutomationStore.Load(root);
        switch (rest.FirstOrDefault())
        {
            case "list" or null:
                foreach (var a in automations)
                    Console.WriteLine(a.Name.PadRight(28) + a.Mode.ToString().ToLowerInvariant().PadRight(7) + a.Steps.Count + " steps"
                                      + (a.Mode == AutomationMode.Timer ? ", cycle " + Automation.DurationLabel(a.CycleSeconds) : ""));
                Console.WriteLine(automations.Count + " automations.");
                return 0;
            case "new-timer" or "new-clock":
            {
                var clock = rest[0] == "new-clock";
                var words = rest.Skip(1).ToList();
                if (words.Count < 2) return Fail(Help);
                var steps = new List<Step>();
                foreach (var spec in words.Skip(1))
                {
                    var eq = spec.LastIndexOf('=');
                    if (eq < 1) return Fail("Expected <wallpaper>=" + (clock ? "HH:MM-HH:MM" : "seconds") + ", got " + spec);
                    if (Pick(spec[..eq], root) is not { } entry) return 1;
                    var value = spec[(eq + 1)..];
                    if (clock)
                    {
                        var match = ClockRange().Match(value);
                        if (!match.Success) return Fail("Time range must look like 07:00-20:00, got " + value);
                        steps.Add(new Step
                        {
                            WallpaperID = entry.Id,
                            StartMinute = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value),
                            EndMinute = int.Parse(match.Groups[3].Value) * 60 + int.Parse(match.Groups[4].Value),
                        });
                    }
                    else
                    {
                        if (ParseSeconds(value) is not { } seconds) return Fail("Duration must be like 10, 30s, 5m or 2h, got " + value);
                        steps.Add(new Step { WallpaperID = entry.Id, Seconds = seconds });
                    }
                }
                var name = Names.Unique(words[0], automations.Select(a => a.Name));
                automations.Add(new Automation { Name = name, Mode = clock ? AutomationMode.Clock : AutomationMode.Timer, Steps = steps });
                AutomationStore.Save(root, automations);
                Console.WriteLine("Created automation " + name + " (" + steps.Count + " steps).");
                return 0;
            }
            case "play":
            {
                if (Find(automations, rest, a => a.Name) is not { } automation) return 1;
                if (automation.Steps.Count == 0) return Fail(automation.Name + " has no steps.");
                ScheduleControl.StartAutomation(root, Surface.Desktop, automation.Id);
                Console.WriteLine("Starting " + automation.Name + ". The engine takes it from here.");
                return 0;
            }
            case "stop":
                ScheduleControl.Stop(root, Surface.Desktop);
                Console.WriteLine("Stopped.");
                return 0;
            case "delete":
            {
                if (Find(automations, rest, a => a.Name) is not { } automation) return 1;
                if (ScheduleStore.Load(root, Surface.Desktop).AutomationID == automation.Id) ScheduleControl.Stop(root, Surface.Desktop, restore: false);
                AutomationStore.Save(root, automations.Where(a => a.Id != automation.Id));
                Console.WriteLine("Deleted " + automation.Name + ".");
                return 0;
            }
        }
        return Fail(Help);
    }

    private static int Status(string root)
    {
        var any = false;
        foreach (var surface in Enum.GetValues<Surface>())
        {
            var state = ScheduleStore.Load(root, surface);
            if (!state.IsActive) continue;
            any = true;
            var name = PlaylistStore.Load(root).FirstOrDefault(p => p.Id == state.PlaylistID)?.Name
                       ?? AutomationStore.Load(root).FirstOrDefault(a => a.Id == state.AutomationID)?.Name ?? "?";
            Console.WriteLine(surface + ": " + (state.PlaylistID is not null ? "playlist " : "automation ") + name
                              + (state.PendingStart ? " (starting)" : ", step " + (state.StepIndex + 1) + " since " + state.StepStartedAt.ToLocalTime().ToString("HH:mm:ss")));
        }
        if (!any) Console.WriteLine("No schedule is running.");
        return 0;
    }

    // ---- Pause After

    private static int PauseAfterCommand(List<string> rest, string root)
    {
        var value = rest.FirstOrDefault(r => !r.StartsWith("--"));
        if (value is null) return Fail(Help);
        var selector = Option(rest, "--wallpaper");
        int? seconds = null;
        var isDefault = value.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (!isDefault)
        {
            if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) seconds = 0;
            else if (ParseSeconds(value) is { } parsed) seconds = parsed;
            else return Fail("Use a time like 30s, 5m or 1h, or off.");
        }

        if (selector is null)
        {
            if (isDefault) return Fail("default only makes sense with --wallpaper.");
            var config = EngineConfig.Load(root);
            config.PauseAfterSeconds = seconds == 0 ? null : seconds;
            config.Save(root);
            Console.WriteLine(seconds == 0 ? "Wallpapers keep playing." : "Wallpapers freeze after " + Automation.DurationLabel(seconds!.Value) + ".");
            return 0;
        }

        if (Pick(selector, root) is not { } entry) return 1;
        LibraryWriter.Update(root, m => { if (m.Find(entry.Id) is { } e) e.PauseAfterSeconds = seconds; });
        Console.WriteLine(entry.Title + ": " + (isDefault ? "follows the global setting" : seconds == 0 ? "never freezes" : "freezes after " + Automation.DurationLabel(seconds!.Value)) + ".");
        return 0;
    }

    // ---- Helpers

    /// A duration as 10, 30s, 5m or 2h.
    internal static int? ParseSeconds(string text)
    {
        var match = DurationText().Match(text.Trim());
        if (!match.Success) return null;
        var number = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToLowerInvariant() switch { "m" => number * 60, "h" => number * 3600, _ => number };
    }

    private static List<string> Positional(List<string> words, params string[] valued) =>
        words.Where((w, i) => !w.StartsWith("--") && (i == 0 || !valued.Contains(words[i - 1]))).ToList();

    private static string? Option(List<string> words, string name)
    {
        var i = words.IndexOf(name);
        return i >= 0 && i + 1 < words.Count ? words[i + 1] : null;
    }

    private static T? Find<T>(IEnumerable<T> items, List<string> rest, Func<T, string> name) where T : class
    {
        var selector = rest.Skip(1).FirstOrDefault(r => !r.StartsWith("--"));
        if (selector is null) { Console.Error.WriteLine("Name one."); return null; }
        var all = items.ToList();
        var match = all.FirstOrDefault(i => name(i).Equals(selector, StringComparison.OrdinalIgnoreCase))
                    ?? all.SingleOrDefault(i => name(i).Contains(selector, StringComparison.OrdinalIgnoreCase));
        if (match is null) Console.Error.WriteLine("Nothing matches \"" + selector + "\".");
        return match;
    }

    private static WallpaperEntry? Pick(string selector, string root)
    {
        var all = LibraryManifest.Load(root).Wallpapers;
        var matches = all.Where(w => w.Id == selector).Take(2).ToList();
        if (matches.Count == 0)
            matches = all.Where(w => (selector.Length >= 6 && w.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase))
                                     || w.Title.Contains(selector, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        var match = matches.Count == 1 ? matches[0] : null;
        if (match is null) Console.Error.WriteLine("\"" + selector + "\" does not match exactly one wallpaper in your library.");
        return match;
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})-(\d{1,2}):(\d{2})$")] private static partial Regex ClockRange();
    [GeneratedRegex(@"^(\d+)\s*([smh]?)$", RegexOptions.IgnoreCase)] private static partial Regex DurationText();
}
