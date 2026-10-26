using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Schedule;
using Muro.Core.Updates;
using Muro.Platform;

namespace Muro.App;

/// Every switch here changes one field and saves at once. The engine watches config.json and applies it,
/// and the schedule runner writes the same file, so nothing is held in memory and saved later.
public sealed partial class SettingsPage : Page
{
    private static readonly (string Label, double Speed)[] Speeds = [("0.5x", 0.5), ("0.75x", 0.75), ("1x", 1.0), ("1.25x", 1.25), ("1.5x", 1.5)];
    private static readonly int[] LowBatteryLevels = [10, 15, 20, 30, 40, 50];
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Services.Updates.Changed -= ShowUpdate;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.Updates.Changed += ShowUpdate;
        Load();
        ShowUpdate();
        // Walking every wallpaper's files takes a moment on a big library.
        var entries = Services.Library.Manifest.Wallpapers.ToList();
        var bytes = await Task.Run(() => entries.Sum(w => LibraryUsage.BytesOf(Services.Root, w)));
        LibrarySize.Text = "Library: " + entries.Count + (entries.Count == 1 ? " wallpaper" : " wallpapers") + ", " + Format.Bytes(bytes);
    }

    private void Load()
    {
        _loading = true;
        var config = EngineConfig.Load(Services.Root);

        StartSwitch.IsOn = StartWithWindows.IsEnabled();
        StartNote.Text = !StartSwitch.IsOn && StartWithWindows.Command() is not null
            ? "Turned off in Windows (Settings, Apps, Startup). Turn it on here to allow it again."
            : "Starts Muro quietly in the background when you sign in";
        TraySwitch.IsOn = config.ShowTrayIcon;
        AccentSwitch.IsOn = config.MatchAccent;

        SpeedBox.ItemsSource = Speeds.Select(s => s.Label).ToList();
        var speed = config.PlaybackSpeed ?? 1.0;
        SpeedBox.SelectedIndex = Array.FindIndex(Speeds, s => Math.Abs(s.Speed - speed) < 0.01) is var i and >= 0 ? i : 2;
        QualityBox.ItemsSource = new[] { "Smooth", "Efficient" };
        QualityBox.SelectedIndex = AppSettings.Load(Services.Root).Efficient ? 1 : 0;

        FullscreenSwitch.IsOn = config.PauseOnFullscreen;
        SaverSwitch.IsOn = config.PauseOnBatterySaver;
        LowBatterySwitch.IsOn = config.PauseOnLowBattery;
        LowBatteryBox.ItemsSource = LowBatteryLevels.Select(p => p + "%").ToList();
        LowBatteryBox.SelectedIndex = Math.Max(0, Array.IndexOf(LowBatteryLevels, config.LowBatteryPercent));
        DesktopOnlySwitch.IsOn = config.PlayOnlyOnDesktop == true;

        DisplaysHost.Children.Clear();
        var monitors = Monitors.All();
        for (var n = 0; n < monitors.Count; n++)
        {
            var m = monitors[n];
            DisplaysHost.Children.Add(new TextBlock { Text = "Display " + (n + 1) + "  -  " + (int)m.Bounds.Width + " x " + (int)m.Bounds.Height + (m.Primary ? "  (main)" : "") });
        }

        LoadSurfaces(config);

        LibraryPath.Text = Services.Root;
        VersionText.Text = "Muro for Windows " + AppVersion.Current;
        _loading = false;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    /// A save that cannot happen (a program has the file open, or it is damaged) is said out loud, and the
    /// page goes back to what is really saved instead of showing a change that did not stick.
    private void Change(Action write)
    {
        try
        {
            write();
            MessageBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowMessage(InfoBarSeverity.Error, "That setting could not be saved", ex.Message);
            Load();
        }
    }

    // ---- General

    private void OnStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (!StartSwitch.IsOn) StartWithWindows.Disable();
            else if (EngineLauncher.Locate() is { } engine) StartWithWindows.Enable(engine);
            else throw new FileNotFoundException("Muro could not find its background engine (Muro.Engine.exe) next to the app.");
            MessageBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowMessage(InfoBarSeverity.Error, "Start with Windows could not be changed", ex.Message);
        }
        Load();
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = TraySwitch.IsOn;
        Change(() => EngineConfig.Update(Services.Root, c => c.ShowTrayIcon = on));
    }

    private void OnAccentToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AccentSwitch.IsOn;
        Change(() =>
        {
            EngineConfig.Update(Services.Root, c => c.MatchAccent = on);
            EngineLauncher.EnsureRunning();   // the engine changes the colour, and puts yours back when this goes off
        });
    }

    // ---- Playback

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpeedBox.SelectedIndex < 0) return;
        var speed = Speeds[SpeedBox.SelectedIndex].Speed;
        Change(() => EngineConfig.Update(Services.Root, c => c.PlaybackSpeed = Math.Abs(speed - 1.0) < 0.01 ? null : speed));
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityBox.SelectedIndex < 0) return;
        var mode = QualityBox.SelectedIndex == 1 ? "efficient" : "smooth";
        Change(() => AppSettings.Update(Services.Root, s => s.DefaultMode = mode));
    }

    // ---- Energy

    private void OnEnergyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Only the field that was touched: the rest of the file may have moved on since this page loaded.
        var toggle = (ToggleSwitch)sender;
        var on = toggle.IsOn;
        Change(() => EngineConfig.Update(Services.Root, c =>
        {
            if (toggle == FullscreenSwitch) c.PauseOnFullscreen = on;
            else if (toggle == SaverSwitch) c.PauseOnBatterySaver = on;
            else if (toggle == LowBatterySwitch) c.PauseOnLowBattery = on;
            else if (toggle == DesktopOnlySwitch) c.PlayOnlyOnDesktop = on ? true : null;
        }));
    }

    private void OnLowBatteryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LowBatteryBox.SelectedIndex < 0) return;
        var percent = LowBatteryLevels[LowBatteryBox.SelectedIndex];
        Change(() => EngineConfig.Update(Services.Root, c => c.LowBatteryPercent = percent));
    }

    // ---- Screen saver and lock screen

    /// One entry in a "Plays" list: the desktop's wallpaper, one wallpaper, a playlist or an automation.
    private sealed record Choice(string Label, string Kind, string? Id);

    private List<Choice> _saverChoices = [], _lockChoices = [];

    private List<Choice> BuildChoices(bool withDesktop)
    {
        var list = new List<Choice>();
        if (withDesktop) list.Add(new("Same as the desktop", "desktop", null));
        list.AddRange(Services.Library.Manifest.Wallpapers.OrderBy(w => w.Title).Select(w => new Choice("Wallpaper: " + w.Title, "wallpaper", w.Id)));
        list.AddRange(PlaylistStore.Load(Services.Root).Select(p => new Choice("Playlist: " + p.Name, "playlist", p.Id)));
        list.AddRange(AutomationStore.Load(Services.Root).Select(a => new Choice("Automation: " + a.Name, "automation", a.Id)));
        return list;
    }

    /// Which entry is current: a running schedule, else the assigned wallpaper, else "same as the desktop".
    private static int Selected(List<Choice> choices, Surface surface, string? assigned)
    {
        var state = ScheduleStore.Load(Services.Root, surface);
        if (state.PlaylistID is { } playlist) return choices.FindIndex(c => c.Kind == "playlist" && c.Id == playlist);
        if (state.AutomationID is { } automation) return choices.FindIndex(c => c.Kind == "automation" && c.Id == automation);
        if (assigned is not null) return choices.FindIndex(c => c.Kind == "wallpaper" && c.Id == assigned);
        return choices.FindIndex(c => c.Kind == "desktop");
    }

    private void LoadSurfaces(EngineConfig config)
    {
        _saverChoices = BuildChoices(withDesktop: true);
        _lockChoices = BuildChoices(withDesktop: false);
        SaverPlaysBox.ItemsSource = _saverChoices.Select(c => c.Label).ToList();
        SaverPlaysBox.SelectedIndex = Selected(_saverChoices, Surface.ScreenSaver, config.ScreenSaver?.WallpaperID);
        LockPlaysBox.ItemsSource = _lockChoices.Select(c => c.Label).ToList();
        LockPlaysBox.SelectedIndex = Selected(_lockChoices, Surface.LockScreen, config.LockScreen?.WallpaperID);
        // Nothing is assigned while the switch is off; show what turning it on would use.
        if (LockPlaysBox.SelectedIndex < 0 && _lockChoices.Count > 0) LockPlaysBox.SelectedIndex = 0;

        var muro = ScreenSaverSetup.IsMuro();
        var windows = ScreenSaverSetup.Read();
        ScreenSaverSwitch.IsOn = muro;
        SaverDelayBox.IsEnabled = muro;
        var delays = ScreenSaverDelay.Choices.ToList();
        var current = windows.Active ? windows.TimeoutSeconds : ScreenSaverDelay.Never;
        if (!delays.Contains(current)) delays.Insert(0, current);
        SaverDelayBox.ItemsSource = delays.Select(ScreenSaverDelay.Label).ToList();
        SaverDelayBox.SelectedIndex = delays.IndexOf(current);
        _delays = delays;
        SaverNote.Text = muro && !windows.Active
            ? "Muro is chosen, but Windows' screen saver is off (Start after: Never)."
            : "Plays a wallpaper on every display when the PC is idle";
        SaverTry.IsEnabled = ScreenSaverFiles.Locate() is not null;

        LockSwitch.IsOn = config.LockScreen is not null || ScheduleStore.Load(Services.Root, Surface.LockScreen).IsActive;
        var mode = LockScreenImage.Mode();
        LockNote.Text = "Windows cannot play video on the lock screen, so it shows one frame of the wallpaper. Your own picture comes back when this is off."
            + (mode != "picture" ? " Your lock screen is set to " + (mode == "spotlight" ? "Windows Spotlight" : "a slideshow") + "; Muro switches it to a picture, and turning this off does not bring that back." : "");
    }

    private List<int> _delays = [];

    /// Applies a choice to a surface. Playlists and automations are started for the engine to carry out; a wallpaper
    /// or "same as the desktop" stops any schedule there and sets the assignment.
    private static void Choose(Surface surface, Choice choice)
    {
        switch (choice.Kind)
        {
            case "playlist": ScheduleControl.StartPlaylist(Services.Root, surface, choice.Id!); break;
            case "automation": ScheduleControl.StartAutomation(Services.Root, surface, choice.Id!); break;
            default:
                ScheduleControl.Stop(Services.Root, surface, restore: false);
                var mode = surface == Surface.ScreenSaver && AppSettings.Load(Services.Root).Efficient ? "efficient" : "smooth";
                EngineConfig.Update(Services.Root, c =>
                {
                    var assignment = choice.Kind == "wallpaper" ? new EngineConfig.Assignment(choice.Id!, mode) : null;
                    if (surface == Surface.ScreenSaver) c.ScreenSaver = assignment; else c.LockScreen = assignment;
                });
                break;
        }
        EngineLauncher.EnsureRunning();   // schedules and the lock screen picture are carried out by the engine
    }

    private void OnSaverToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ScreenSaverSwitch.IsOn;
        Change(() =>
        {
            if (on)
            {
                var scr = ScreenSaverFiles.Locate() ?? throw new FileNotFoundException("Muro could not find its screen saver (Muro.scr) next to the app.");
                var now = ScreenSaverSetup.Read();
                // Keep what Windows had, once, so turning Muro off puts it back.
                if (!ScreenSaverSetup.IsMuro())
                    AppSettings.Update(Services.Root, s => s.ScreenSaverBefore = new ScreenSaverBackup { Exe = now.Exe, Active = now.Active, TimeoutSeconds = now.TimeoutSeconds });
                ScreenSaverSetup.Use(scr, now.Active && now.TimeoutSeconds > 0 ? now.TimeoutSeconds : 600);
            }
            else
            {
                var before = AppSettings.Load(Services.Root).ScreenSaverBefore;
                ScreenSaverSetup.Restore(before is null ? new ScreenSaverSetup.State(null, false, 0) : new ScreenSaverSetup.State(before.Exe, before.Active, before.TimeoutSeconds));
                AppSettings.Update(Services.Root, s => s.ScreenSaverBefore = null);
            }
        });
        Load();
    }

    private void OnSaverDelayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SaverDelayBox.SelectedIndex < 0 || SaverDelayBox.SelectedIndex >= _delays.Count) return;
        var seconds = _delays[SaverDelayBox.SelectedIndex];
        Change(() => ScreenSaverSetup.SetDelay(seconds));
        Load();
    }

    private void OnSaverPlaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SaverPlaysBox.SelectedIndex < 0) return;
        var choice = _saverChoices[SaverPlaysBox.SelectedIndex];
        Change(() => Choose(Surface.ScreenSaver, choice));
    }

    private void OnSaverTry(object sender, RoutedEventArgs e)
    {
        if (ScreenSaverFiles.Locate() is { } scr) Process.Start(new ProcessStartInfo(scr, "/s") { UseShellExecute = false });
    }

    private void OnLockToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = LockSwitch.IsOn;
        Change(() =>
        {
            if (on)
            {
                var choice = LockPlaysBox.SelectedIndex >= 0 ? _lockChoices[LockPlaysBox.SelectedIndex] : _lockChoices.FirstOrDefault();
                Choose(Surface.LockScreen, choice ?? throw new IOException("Add a wallpaper to your library first."));
            }
            else
            {
                ScheduleControl.Stop(Services.Root, Surface.LockScreen, restore: false);
                EngineConfig.Update(Services.Root, c => c.LockScreen = null);
                EngineLauncher.EnsureRunning();   // the engine puts the original picture back
            }
        });
        Load();
    }

    private void OnLockPlaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LockPlaysBox.SelectedIndex < 0 || !LockSwitch.IsOn) return;
        var choice = _lockChoices[LockPlaysBox.SelectedIndex];
        Change(() => Choose(Surface.LockScreen, choice));
    }

    // ---- Storage

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Services.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Services.Root + "\""));
    }

    // ---- About and updates

    private void ShowUpdate()
    {
        var updates = Services.Updates;
        CheckButton.IsEnabled = updates.Current != UpdateService.State.Checking;
        var available = updates.Current == UpdateService.State.Available && updates.Latest is not null;
        NotesButton.Visibility = DownloadButton.Visibility = Format.Show(available);
        UpdateText.Text = updates.Current switch
        {
            UpdateService.State.Checking => "Checking for updates...",
            UpdateService.State.UpToDate => "Muro is up to date.",
            UpdateService.State.Failed => "Muro could not check for updates. Check your connection and try again.",
            UpdateService.State.Available when updates.Latest is { } release => "Muro " + release.Version + " is available.",
            _ => "Muro looks for updates by itself every few hours.",
        };
    }

    private async void OnCheck(object sender, RoutedEventArgs e) => await Services.Updates.CheckAsync(userInitiated: true);

    private async void OnNotes(object sender, RoutedEventArgs e)
    {
        if (Services.Updates.Latest is { } release) await WhatsNewDialog.ShowAsync(XamlRoot, release);
    }

    private void OnDownload(object sender, RoutedEventArgs e) => Services.Updates.OpenDownload();
}
