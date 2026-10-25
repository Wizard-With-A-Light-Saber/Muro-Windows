using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core.Library;
using Muro.Core.Schedule;

namespace Muro.App;

/// The playlist editor, a dialog: name, how often it changes, shuffle, and the wallpapers in order.
internal static class PlaylistEditor
{
    /// Returns the edited playlist, or null when cancelled. The caller saves it.
    public static async Task<Playlist?> ShowAsync(XamlRoot root, Playlist? existing, IReadOnlyList<string> otherNames)
    {
        var library = Services.Library.Manifest.Wallpapers;
        var chosen = (existing?.WallpaperIDs ?? []).Where(id => library.Any(w => w.Id == id)).ToList();

        var name = new TextBox { Header = "Name", Text = existing?.Name ?? Names.Unique("New Playlist", otherNames) };
        var minutes = new NumberBox
        {
            Header = "Change wallpaper every (minutes)", Minimum = 1, Maximum = 1440, Value = existing?.IntervalMinutes ?? 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 5, LargeChange = 15, Width = 260,
        };
        var shuffle = new ToggleSwitch { Header = "Shuffle", IsOn = existing?.Shuffle ?? false };
        var rows = new StackPanel { Spacing = 6 };
        var empty = new TextBlock { Text = "Add at least one wallpaper.", Opacity = 0.7 };
        var picker = new ComboBox { Header = "Add a wallpaper", PlaceholderText = "Choose...", Width = 320, ItemsSource = library.OrderBy(w => w.Title).ToList(), DisplayMemberPath = "Title" };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = existing is null ? "New playlist" : "Edit playlist",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = chosen.Count > 0,
        };

        void Refresh()
        {
            rows.Children.Clear();
            for (var i = 0; i < chosen.Count; i++)
            {
                var index = i;
                var title = library.FirstOrDefault(w => w.Id == chosen[index])?.Title ?? chosen[index];
                var up = new Button { Content = "Up", IsEnabled = index > 0 };
                up.Click += (_, _) => { (chosen[index - 1], chosen[index]) = (chosen[index], chosen[index - 1]); Refresh(); };
                var down = new Button { Content = "Down", IsEnabled = index < chosen.Count - 1 };
                down.Click += (_, _) => { (chosen[index + 1], chosen[index]) = (chosen[index], chosen[index + 1]); Refresh(); };
                var remove = new Button { Content = "Remove" };
                remove.Click += (_, _) => { chosen.RemoveAt(index); Refresh(); };

                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (var c = 0; c < 3; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBlock { Text = (index + 1) + ".  " + title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                row.Children.Add(label);
                Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); Grid.SetColumn(remove, 3);
                row.Children.Add(up); row.Children.Add(down); row.Children.Add(remove);
                rows.Children.Add(row);
            }
            empty.Visibility = chosen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = chosen.Count > 0;
        }

        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is WallpaperEntry entry && !chosen.Contains(entry.Id)) chosen.Add(entry.Id);
            picker.SelectedItem = null;
            Refresh();
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(name);
        panel.Children.Add(minutes);
        panel.Children.Add(shuffle);
        panel.Children.Add(new TextBlock { Text = shuffle.IsOn ? "" : "Plays in the order below.", Opacity = 0.7, Visibility = Visibility.Collapsed });
        panel.Children.Add(empty);
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 240 });
        panel.Children.Add(picker);
        dialog.Content = panel;
        Refresh();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var finalName = name.Text.Trim().Length == 0 ? Names.Unique("New Playlist", otherNames) : name.Text.Trim();
        if (Names.IsTaken(finalName, otherNames.Select(n => ("", n)), existing?.Id)) finalName = Names.Unique(finalName, otherNames);
        return (existing ?? new Playlist()) with
        {
            Name = finalName,
            IntervalMinutes = (int)Math.Max(1, Math.Round(double.IsNaN(minutes.Value) ? 30 : minutes.Value)),
            Shuffle = shuffle.IsOn,
            WallpaperIDs = chosen,
        };
    }
}
