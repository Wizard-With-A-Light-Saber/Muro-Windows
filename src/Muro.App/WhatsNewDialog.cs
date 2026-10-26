using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core.Updates;

namespace Muro.App;

/// The release notes of the update on offer, with the download one click away. Only one dialog can be
/// open at a time in a window, so a second request while one is showing is ignored.
internal static class WhatsNewDialog
{
    private static bool _open;

    public static async Task ShowAsync(XamlRoot root, ReleaseInfo release)
    {
        if (_open) return;
        _open = true;
        try
        {
            var panel = new StackPanel { Spacing = 6 };
            if (release.Title is { } title) panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (release.PublishedAt is { } at) panel.Children.Add(new TextBlock { Text = at.LocalDateTime.ToString("d MMMM yyyy"), Opacity = 0.7 });
            if (release.IsEmpty)
                panel.Children.Add(new TextBlock { Text = "No release notes were written for this version.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            foreach (var section in release.Notes)
            {
                if (section.Name is { } name)
                    panel.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
                foreach (var line in section.Lines)
                    panel.Children.Add(new TextBlock { Text = "-  " + line, TextWrapping = TextWrapping.Wrap });
            }

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Muro " + release.Version + " is available",
                Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                PrimaryButtonText = "Download",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) Services.Updates.OpenDownload();
            else Services.Updates.MarkSeen();
        }
        finally { _open = false; }
    }
}
