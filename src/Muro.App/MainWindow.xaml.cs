using Microsoft.UI.Windowing;
using Muro.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Muro.App;

public sealed partial class MainWindow : Window
{
    internal static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1280, 840));
        Title = "Muro";
        var icon = Path.Combine(AppContext.BaseDirectory, "muro.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        // New wallpapers and new releases appear without waiting for a restart: look again whenever the
        // window comes to the front. Both are throttled, so rapid focus changes cost nothing.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            _ = Services.Catalog.RefreshAsync();
            _ = Services.Updates.CheckIfDueAsync();
        };
        Services.Updates.Changed += ShowUpdateBar;
        ApplyTheme();
        Loc.Use(AppSettings.Load(Services.Root).Language);
        ContentFrame.Navigated += (_, e) => { if (e.Content is Page page) Localizer.Attach(page, DispatcherQueue); };
        Nav.Loaded += (_, _) => ApplyLanguage();
        Nav.SelectedItem = Nav.MenuItems[0];
        _ = Services.Catalog.RefreshAsync(force: true);
        _ = Services.Updates.CheckIfDueAsync();
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// Puts the window and the page on show into the chosen language (a code, or null for the Windows language). Pages
    /// opened later translate themselves as they load.
    internal void ApplyLanguage()
    {
        Loc.Use(AppSettings.Load(Services.Root).Language);
        Localizer.Apply(Content);
        if (Nav.SettingsItem is NavigationViewItem settings) settings.Content = Loc.T("Settings");
    }

    /// Light, Dark, or System (follows Windows and switches the moment Windows does). Applied to the whole window.
    internal void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = AppSettings.Load(Services.Root).Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ContentFrame.Navigate(tag switch
        {
            "library" => typeof(LibraryPage),
            "schedules" => typeof(SchedulesPage),
            "explore" => typeof(ExplorePage),
            _ => typeof(FeaturedPage),
        });
    }

    // ---- Updates

    private void ShowUpdateBar()
    {
        var updates = Services.Updates;
        if (updates.BannerVisible && updates.Latest is { } release)
        {
            UpdateBar.Title = "Muro " + release.Version + " is available";
            UpdateBar.Message = release.Title ?? "See what changed, then download it.";
            UpdateBar.IsOpen = true;
        }
        else UpdateBar.IsOpen = false;
    }

    private async void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (Services.Updates.Latest is { } release) await WhatsNewDialog.ShowAsync(Content.XamlRoot, release);
    }

    /// Closing the banner is how someone says "I know": it is not shown again for this version.
    private void OnUpdateBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) Services.Updates.MarkSeen();
    }
}
