using Microsoft.UI.Xaml;

namespace Muro.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        // A file another program has open must never close the app. Everything else still crashes as before,
        // but leaves a line in app.log first.
        UnhandledException += (_, e) =>
        {
            AppLog.Info("unhandled: " + e.Exception);
            if (e.Exception is IOException) e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
