using Microsoft.UI.Windowing;
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

        // New wallpapers appear without an app update: look again whenever the window comes to the front.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                _ = Services.Catalog.RefreshAsync();
        };
        Nav.SelectedItem = Nav.MenuItems[0];
        _ = Services.Catalog.RefreshAsync(force: true);
    }

    internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ContentFrame.Navigate(tag switch { "library" => typeof(LibraryPage), "schedules" => typeof(SchedulesPage), _ => typeof(ExplorePage) });
    }
}
