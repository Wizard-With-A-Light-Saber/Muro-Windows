using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Muro.Core.Library;
using Muro.Core.Schedule;

namespace Muro.App;

/// Edits one automation. Timer mode is a list of wallpapers with a length each; clock mode adds the
/// 24-hour timeline and a start and end time per wallpaper.
public sealed partial class AutomationEditorPage : Page
{
    private Automation _automation = new();
    private bool _isNew = true;
    private bool _building;
    private readonly DayTimeline _timeline = new();
    private IReadOnlyList<WallpaperEntry> _library = [];

    public AutomationEditorPage()
    {
        InitializeComponent();
        TimelineHost.Content = _timeline;
        _timeline.StepsChanged += steps => { _automation = _automation with { Steps = steps }; BuildRows(); UpdateGaps(); };
        _timeline.SelectionChanged += _ => { };
    }

    private bool IsClock => _automation.Mode == AutomationMode.Clock;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _library = Services.Library.Manifest.Wallpapers.OrderBy(w => w.Title).ToList();
        _timeline.NameOf = id => _library.FirstOrDefault(w => w.Id == id)?.Title ?? "(deleted wallpaper)";
        AddBox.ItemsSource = _library;

        if (e.Parameter is string id && AutomationStore.Load(Services.Root).FirstOrDefault(a => a.Id == id) is { } existing)
        {
            _automation = existing with { Steps = existing.Steps.ToList() };
            _isNew = false;
        }
        else
        {
            _automation = new Automation { Name = Names.Unique("New Automation", AutomationStore.Load(Services.Root).Select(a => a.Name)) };
        }

        _building = true;
        NameBox.Text = _automation.Name;
        ModeButtons.SelectedIndex = IsClock ? 1 : 0;
        _building = false;
        ApplyMode();
    }

    // ---- Mode

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        var mode = ModeButtons.SelectedIndex == 1 ? AutomationMode.Clock : AutomationMode.Timer;
        if (mode == _automation.Mode) return;
        _automation = _automation with { Mode = mode };
        ApplyMode();
    }

    private void ApplyMode()
    {
        // Switching mode never loses what was typed: each mode keeps its own fields on the step.
        if (IsClock && _automation.Steps.Any(s => s.StartMinute is null || s.EndMinute is null))
            _automation = _automation with { Steps = Automation.FittedToDay(_automation.Steps) };
        ClockPanel.Visibility = Format.Show(IsClock);
        _timeline.Steps = _automation.Steps;
        BuildRows();
        UpdateGaps();
    }

    private void UpdateGaps()
    {
        if (!IsClock) return;
        var gaps = _automation.UncoveredWindows();
        GapText.Text = gaps.Count == 0
            ? "The whole day is covered."
            : "No wallpaper from " + string.Join(", ", gaps.Select(g => Automation.ClockLabel(g.Start) + " to " + Automation.ClockLabel(g.End))) + ": the last one keeps playing.";
    }

    // ---- Steps

    private void OnAddChosen(object sender, SelectionChangedEventArgs e)
    {
        if (AddBox.SelectedItem is not WallpaperEntry entry) return;
        AddBox.SelectedItem = null;
        var step = new Step { WallpaperID = entry.Id, Seconds = 600 };
        var steps = IsClock ? Automation.FittedAdding(step, _automation.Steps) : [.. _automation.Steps, step];
        Replace(steps);
    }

    private void Replace(List<Step> steps)
    {
        _automation = _automation with { Steps = steps };
        _timeline.Steps = steps;
        BuildRows();
        UpdateGaps();
    }

    private void BuildRows()
    {
        RowsHost.Children.Clear();
        var steps = _automation.Steps;
        for (var i = 0; i < steps.Count; i++)
        {
            var index = i;
            var step = steps[i];

            // Top line: which wallpaper, and moving or removing it. Second line: when, or for how long.
            var top = new Grid { ColumnSpacing = 8 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var c = 0; c < 3; c++) top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var wallpaper = new ComboBox { ItemsSource = _library, DisplayMemberPath = "Title", SelectedItem = _library.FirstOrDefault(w => w.Id == step.WallpaperID), HorizontalAlignment = HorizontalAlignment.Stretch };
            wallpaper.SelectionChanged += (_, _) =>
            {
                if (wallpaper.SelectedItem is not WallpaperEntry chosen) return;
                var next = _automation.Steps.ToList();
                next[index] = next[index] with { WallpaperID = chosen.Id };
                _automation = _automation with { Steps = next };
                _timeline.Steps = next;
            };
            top.Children.Add(wallpaper);

            var up = new Button { Content = "Up", IsEnabled = index > 0 };
            up.Click += (_, _) => Replace(Automation.Reordered(_automation.Steps, index, index - 1, carryingSlots: IsClock));
            var down = new Button { Content = "Down", IsEnabled = index < steps.Count - 1 };
            down.Click += (_, _) => Replace(Automation.Reordered(_automation.Steps, index, index + 1, carryingSlots: IsClock));
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                var current = _automation.Steps[index];
                Replace(IsClock ? Automation.FittedRemoving(current, _automation.Steps) : _automation.Steps.Where((_, k) => k != index).ToList());
            };
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); Grid.SetColumn(remove, 3);
            top.Children.Add(up); top.Children.Add(down); top.Children.Add(remove);

            var detail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (IsClock)
            {
                var start = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = TimeSpan.FromMinutes(step.Start) };
                var end = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 5, Time = TimeSpan.FromMinutes(step.End) };
                void Changed()
                {
                    var next = _automation.Steps.ToList();
                    next[index] = next[index] with { StartMinute = (int)start.Time.TotalMinutes, EndMinute = (int)end.Time.TotalMinutes };
                    _automation = _automation with { Steps = next };
                    _timeline.Steps = next;
                    UpdateGaps();
                }
                start.TimeChanged += (_, _) => Changed();
                end.TimeChanged += (_, _) => Changed();
                detail.Children.Add(new TextBlock { Text = "From", VerticalAlignment = VerticalAlignment.Center });
                detail.Children.Add(start);
                detail.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center });
                detail.Children.Add(end);
                detail.Children.Add(new TextBlock { Text = Automation.IsAllDay(step) ? "(all day)" : "", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var seconds = step.Duration;
                var unitSeconds = seconds % 3600 == 0 ? 3600 : seconds % 60 == 0 ? 60 : 1;
                var amount = new NumberBox { Minimum = 1, Value = seconds / unitSeconds, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
                var unit = new ComboBox { ItemsSource = new[] { "seconds", "minutes", "hours" }, SelectedIndex = unitSeconds == 3600 ? 2 : unitSeconds == 60 ? 1 : 0, Width = 120 };
                void Changed()
                {
                    var perUnit = unit.SelectedIndex == 2 ? 3600 : unit.SelectedIndex == 1 ? 60 : 1;
                    var value = double.IsNaN(amount.Value) ? 1 : amount.Value;
                    var next = _automation.Steps.ToList();
                    next[index] = next[index] with { Seconds = (int)Math.Clamp(value * perUnit, 10, 86_400) };
                    _automation = _automation with { Steps = next };
                }
                amount.ValueChanged += (_, _) => Changed();
                unit.SelectionChanged += (_, _) => Changed();
                detail.Children.Add(new TextBlock { Text = "Plays for", VerticalAlignment = VerticalAlignment.Center });
                detail.Children.Add(amount);
                detail.Children.Add(unit);
            }

            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(top);
            card.Children.Add(detail);
            RowsHost.Children.Add(new Border
            {
                Padding = new Thickness(16),
                CornerRadius = (CornerRadius)Application.Current.Resources["MuroPanelRadius"],
                BorderThickness = new Thickness(1),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = card,
            });
        }
    }

    // ---- Save and leave

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_automation.Steps.Count == 0)
        {
            Problem.Title = "Add at least one wallpaper";
            Problem.Message = "An automation with nothing in it cannot run.";
            Problem.IsOpen = true;
            return;
        }
        var all = AutomationStore.Load(Services.Root);
        var others = all.Where(a => a.Id != _automation.Id).Select(a => a.Name).ToList();
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || others.Contains(name, StringComparer.OrdinalIgnoreCase)) name = Names.Unique(name.Length == 0 ? "New Automation" : name, others);
        var saved = _automation with { Name = name };

        var index = all.FindIndex(a => a.Id == saved.Id);
        if (index >= 0) all[index] = saved; else all.Add(saved);
        AutomationStore.Save(Services.Root, all);
        Frame.Navigate(typeof(SchedulesPage));
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SchedulesPage));
}
