using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core;
using Muro.Core.Library;
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
        if (!_loading) EngineConfig.Update(Services.Root, c => c.ShowTrayIcon = TraySwitch.IsOn);
    }

    // ---- Playback

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpeedBox.SelectedIndex < 0) return;
        var speed = Speeds[SpeedBox.SelectedIndex].Speed;
        EngineConfig.Update(Services.Root, c => c.PlaybackSpeed = Math.Abs(speed - 1.0) < 0.01 ? null : speed);
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || QualityBox.SelectedIndex < 0) return;
        var mode = QualityBox.SelectedIndex == 1 ? "efficient" : "smooth";
        AppSettings.Update(Services.Root, s => s.DefaultMode = mode);
    }

    // ---- Energy

    private void OnEnergyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Only the field that was touched: the rest of the file may have moved on since this page loaded.
        var toggle = (ToggleSwitch)sender;
        var on = toggle.IsOn;
        EngineConfig.Update(Services.Root, c =>
        {
            if (toggle == FullscreenSwitch) c.PauseOnFullscreen = on;
            else if (toggle == SaverSwitch) c.PauseOnBatterySaver = on;
            else if (toggle == LowBatterySwitch) c.PauseOnLowBattery = on;
            else if (toggle == DesktopOnlySwitch) c.PlayOnlyOnDesktop = on ? true : null;
        });
    }

    private void OnLowBatteryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LowBatteryBox.SelectedIndex < 0) return;
        var percent = LowBatteryLevels[LowBatteryBox.SelectedIndex];
        EngineConfig.Update(Services.Root, c => c.LowBatteryPercent = percent);
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
