using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core;
using Muro.Core.Schedule;

namespace Muro.App;

/// Playlists, automations, what is running now, and the global Pause After setting.
public sealed partial class SchedulesPage : Page
{
    private static readonly (string Label, int Seconds)[] PauseChoices =
    [
        (Loc.T("Off (keep playing)"), 0), (Loc.T("After 10 seconds"), 10), (Loc.T("After 30 seconds"), 30), (Loc.T("After 1 minute"), 60),
        (Loc.T("After 2 minutes"), 120), (Loc.T("After 5 minutes"), 300), (Loc.T("After 15 minutes"), 900), (Loc.T("After 1 hour"), 3600),
    ];

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading;

    public SchedulesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Services.Library.Reload(raise: false); LoadSettings(); Rebuild(); _poll.Tick += OnPoll; _poll.Start(); };
        Unloaded += (_, _) => { _poll.Stop(); _poll.Tick -= OnPoll; };
    }

    private void OnPoll(object? sender, object e) => UpdateRunning();

    // ---- Pause After

    private void LoadSettings()
    {
        _loading = true;
        var config = EngineConfig.Load(Services.Root);
        PauseAfterBox.ItemsSource = PauseChoices.Select(c => c.Label).ToList();
        var current = PauseAfter.Normalise(config.PauseAfterSeconds) ?? 0;
        var index = Array.FindIndex(PauseChoices, c => c.Seconds == current);
        PauseAfterBox.SelectedIndex = index >= 0 ? index : 0;
        ReplaySwitch.IsOn = config.ReplayOnClearDesktop == true;
        _loading = false;
    }

    private void OnPauseAfterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PauseAfterBox.SelectedIndex < 0) return;
        var seconds = PauseChoices[PauseAfterBox.SelectedIndex].Seconds;
        SaveSetting(c => c.PauseAfterSeconds = seconds == 0 ? null : seconds);
    }

    private void OnReplayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = ReplaySwitch.IsOn;
        SaveSetting(c => c.ReplayOnClearDesktop = on ? true : null);
    }

    /// One field, saved at once. If the file cannot be written (a program has it open, or it is damaged) the
    /// controls go back to what is really saved instead of showing a change that did not stick.
    private void SaveSetting(Action<EngineConfig> change)
    {
        try { EngineConfig.Update(Services.Root, change); }
        catch (IOException ex)
        {
            AppLog.Info("setting not saved: " + ex.Message);
            LoadSettings();
        }
    }

    // ---- The lists

    private void Rebuild()
    {
        var state = ScheduleStore.Load(Services.Root, Surface.Desktop);
        var playlists = PlaylistStore.Load(Services.Root);
        var automations = AutomationStore.Load(Services.Root);

        PlaylistsHost.Children.Clear();
        foreach (var playlist in playlists)
        {
            var p = playlist;
            var summary = p.WallpaperIDs.Count + (p.WallpaperIDs.Count == 1 ? " wallpaper" : " wallpapers") + ", every " + p.IntervalMinutes + " min, " + (p.Shuffle ? "shuffled" : "in order");
            PlaylistsHost.Children.Add(Card(p.Name, summary, state.PlaylistID == p.Id,
                play: () => Start(() => ScheduleControl.StartPlaylist(Services.Root, Surface.Desktop, p.Id)),
                edit: () => _ = EditPlaylistAsync(p),
                delete: () => _ = DeleteAsync(p.Name, () =>
                {
                    if (state.PlaylistID == p.Id) ScheduleControl.Stop(Services.Root, Surface.Desktop, restore: false);
                    PlaylistStore.Save(Services.Root, PlaylistStore.Load(Services.Root).Where(x => x.Id != p.Id));
                })));
        }
        NoPlaylists.Visibility = Format.Show(playlists.Count == 0);

        AutomationsHost.Children.Clear();
        foreach (var automation in automations)
        {
            var a = automation;
            var summary = (a.Mode == AutomationMode.Clock ? "By the clock" : "By timer, a cycle of " + Automation.DurationLabel(a.CycleSeconds)) + ", " + a.Steps.Count + (a.Steps.Count == 1 ? " wallpaper" : " wallpapers");
            AutomationsHost.Children.Add(Card(a.Name, summary, state.AutomationID == a.Id,
                play: () => Start(() => ScheduleControl.StartAutomation(Services.Root, Surface.Desktop, a.Id)),
                edit: () => Frame.Navigate(typeof(AutomationEditorPage), a.Id),
                delete: () => _ = DeleteAsync(a.Name, () =>
                {
                    if (state.AutomationID == a.Id) ScheduleControl.Stop(Services.Root, Surface.Desktop, restore: false);
                    AutomationStore.Save(Services.Root, AutomationStore.Load(Services.Root).Where(x => x.Id != a.Id));
                })));
        }
        NoAutomations.Visibility = Format.Show(automations.Count == 0);
        UpdateRunning();
    }

    private Border Card(string title, string subtitle, bool running, Action play, Action edit, Action delete)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = subtitle + (running ? "  -  running now" : ""), Opacity = 0.75, FontSize = 12 });

        Button Make(string label, Action action, bool accent = false)
        {
            var button = new Button { Content = label };
            if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) => action();
            return button;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(running ? Make(Loc.T("Stop"), () => { ScheduleControl.Stop(Services.Root, Surface.Desktop); Rebuild(); }) : Make(Loc.T("Play"), play, accent: true));
        buttons.Children.Add(Make(Loc.T("Edit"), edit));
        buttons.Children.Add(Make("Delete", delete));

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return new Border
        {
            Padding = new Thickness(20, 16, 20, 16),
            CornerRadius = (CornerRadius)Application.Current.Resources["MuroPanelRadius"],
            BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = grid,
        };
    }

    private async void Start(Action start)
    {
        start();
        EngineLauncher.EnsureRunning();
        await Task.Delay(500);
        Rebuild();
    }

    // ---- Playlists

    private async void OnNewPlaylist(object sender, RoutedEventArgs e) => await EditPlaylistAsync(null);

    private async Task EditPlaylistAsync(Playlist? existing)
    {
        if (Services.Library.Manifest.Wallpapers.Count == 0)
        {
            await new ContentDialog { XamlRoot = XamlRoot, Title = "No wallpapers yet", Content = "Download or import a wallpaper first, then add it to a playlist.", CloseButtonText = "OK" }.ShowAsync();
            return;
        }
        var all = PlaylistStore.Load(Services.Root);
        var others = all.Where(p => p.Id != existing?.Id).Select(p => p.Name).ToList();
        if (await PlaylistEditor.ShowAsync(XamlRoot, existing, others) is not { } saved) return;
        var index = all.FindIndex(p => p.Id == saved.Id);
        if (index >= 0) all[index] = saved; else all.Add(saved);
        PlaylistStore.Save(Services.Root, all);
        Rebuild();
    }

    // ---- Automations and deleting

    private void OnNewAutomation(object sender, RoutedEventArgs e)
    {
        if (Services.Library.Manifest.Wallpapers.Count == 0)
        {
            _ = new ContentDialog { XamlRoot = XamlRoot, Title = "No wallpapers yet", Content = "Download or import a wallpaper first, then add it to an automation.", CloseButtonText = "OK" }.ShowAsync();
            return;
        }
        Frame.Navigate(typeof(AutomationEditorPage), null);
    }

    private async Task DeleteAsync(string name, Action delete)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete \"" + name + "\"?",
            Content = "The wallpapers in it are not deleted. If it is running, it stops.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        delete();
        Rebuild();
    }

    // ---- What is running

    private void OnStop(object sender, RoutedEventArgs e)
    {
        ScheduleControl.Stop(Services.Root, Surface.Desktop);
        Rebuild();
    }

    private void UpdateRunning()
    {
        var state = ScheduleStore.Load(Services.Root, Surface.Desktop);
        if (!state.IsActive) { RunningBar.IsOpen = false; return; }
        var name = PlaylistStore.Load(Services.Root).FirstOrDefault(p => p.Id == state.PlaylistID)?.Name
                   ?? AutomationStore.Load(Services.Root).FirstOrDefault(a => a.Id == state.AutomationID)?.Name ?? "A schedule";
        RunningBar.Title = name + " is running on your desktop";
        RunningBar.Message = "Stopping it puts back the wallpaper that was there before.";
        RunningBar.IsOpen = true;
    }
}
