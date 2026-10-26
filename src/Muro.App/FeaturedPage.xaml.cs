using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Muro.Core;
using Muro.Core.Catalog;
using Muro.Core.Library;
using Muro.Core.Media;

namespace Muro.App;

/// The first tab: a banner, the newest drop and today's Pick.
///
/// The banner follows the macOS rules. It only ever shows wallpapers the user has downloaded, never a
/// video from the internet, and never one of their own imports (those stay in the Library). Before the
/// first download there is nothing local to show, so it offers the newest catalog wallpaper as a still
/// picture with a Download button, and the video is only fetched if the visitor asks to preview it.
public sealed partial class FeaturedPage : Page
{
    private readonly Dictionary<string, CatalogItemVm> _vms = [];
    private WallpaperEntry? _hero;
    private CatalogEntry? _heroRemote;
    private bool _applying;

    public FeaturedPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.Catalog.Changed += Rebuild;
        Services.Library.Changed += Rebuild;
        Services.Downloads.Changed += OnDownloadChanged;
        Services.Downloads.Failed += OnDownloadFailed;
        Rebuild();
        _ = Services.Catalog.RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Services.Catalog.Changed -= Rebuild;
        Services.Library.Changed -= Rebuild;
        Services.Downloads.Changed -= OnDownloadChanged;
        Services.Downloads.Failed -= OnDownloadFailed;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    // ---- Building the page

    private void Rebuild()
    {
        var catalog = Services.Catalog;
        var items = catalog.Items;

        if (catalog.Error is { } error)
        {
            ErrorBar.Title = error.Title;
            ErrorBar.Message = error.Detail;
            ErrorBar.IsOpen = true;
        }
        else ErrorBar.IsOpen = false;

        // The banner: the chosen wallpaper, else what is playing, else the newest download.
        var local = Services.Library.Manifest.Wallpapers.Where(w => !w.IsImported).OrderByDescending(w => w.DateAdded).ToList();
        var playing = PlaybackService.PlayingIds();
        var chosen = AppSettings.Load(Services.Root).HeroId;
        _hero = local.FirstOrDefault(w => w.Id == chosen) ?? local.FirstOrDefault(w => playing.Contains(w.Id)) ?? local.FirstOrDefault();
        _heroRemote = _hero is null ? Featured.LatestDrop(items).FirstOrDefault() ?? items.FirstOrDefault() : null;
        ShowHero(playing);

        HeroStrip.ItemsSource = local.Select(w => new LibraryItemVm(w, 0, playing.Contains(w.Id))).ToList();
        HeroStrip.Visibility = Format.Show(local.Count > 1);

        var draw = AppSettings.Load(Services.Root).Pick;
        var before = (draw.Day, string.Join(',', draw.Ids));
        var drop = Featured.LatestDrop(items);
        var picks = Featured.Pick(items, draw, DateOnly.FromDateTime(DateTime.Now), Random.Shared);
        if ((draw.Day, string.Join(',', draw.Ids)) != before)
        {
            // Remembering the draw is a courtesy: if the file is busy the same draw is simply made again.
            try { AppSettings.Update(Services.Root, s => s.Pick = draw); }
            catch (IOException) { }
        }
        Fill(DropTitle, DropList, drop);
        Fill(PickTitle, PickList, picks);

        var nothing = _hero is null && _heroRemote is null;
        // Collapsed as well as inactive: an idle ProgressRing still takes its space and pushes the banner down.
        Spinner.Visibility = Format.Show(nothing && !catalog.Loaded);
        Spinner.IsActive = Spinner.Visibility == Visibility.Visible;
        EmptyText.Visibility = Format.Show(nothing && catalog.Loaded);
    }

    private void Fill(TextBlock title, ListView list, IReadOnlyList<CatalogEntry> entries)
    {
        foreach (var entry in entries)
            if (!_vms.ContainsKey(entry.Id)) _vms[entry.Id] = new CatalogItemVm(entry);
        var vms = entries.Select(e => _vms[e.Id]).ToList();
        foreach (var vm in vms) vm.Refresh();
        list.ItemsSource = vms;
        title.Visibility = list.Visibility = Format.Show(vms.Count > 0);
    }

    private void ShowHero(HashSet<string> playing)
    {
        Hero.Visibility = Format.Show(_hero is not null || _heroRemote is not null);
        if (_hero is { } entry)
        {
            var thumb = string.IsNullOrEmpty(entry.Thumbnail) ? null : LibraryManifest.Resolve(Services.Root, entry.Thumbnail);
            HeroImage.Source = thumb is not null && File.Exists(thumb) ? new BitmapImage { DecodePixelWidth = 1600, UriSource = new Uri(thumb) } : null;
            HeroTitle.Text = entry.Title;
            HeroMeta.Text = entry.Category + "  -  " + Format.Resolution(entry.Width, entry.Height, entry.Fps);
            var isPlaying = playing.Contains(entry.Id);
            HeroEyebrow.Text = isPlaying ? "PLAYING NOW" : "FROM YOUR LIBRARY";
            HeroPrimary.Content = isPlaying ? "Apply again" : "Apply";
            HeroPrimary.IsEnabled = !_applying;
            HeroProgress.Visibility = Visibility.Collapsed;
        }
        else if (_heroRemote is { } remote)
        {
            HeroImage.Source = new BitmapImage { DecodePixelWidth = 1600, UriSource = new Uri(remote.Thumbnail) };
            HeroTitle.Text = remote.Title;
            HeroMeta.Text = remote.Category + "  -  " + Format.Resolution(remote.Width, remote.Height, remote.Fps) + "  " + Format.Bytes(remote.SizeBytes);
            HeroEyebrow.Text = "NEW IN MURO";
            ShowHeroDownload(remote.Id);
        }
    }

    private void ShowHeroDownload(string id)
    {
        var downloading = Services.Downloads.IsActive(id);
        HeroPrimary.Content = downloading ? "Downloading..." : "Download";
        HeroPrimary.IsEnabled = !downloading;
        HeroProgress.Visibility = Format.Show(downloading);
        HeroProgress.Value = Services.Downloads.ProgressOf(id) * 100;
    }

    // ---- The banner

    private async void OnHeroPrimary(object sender, RoutedEventArgs e)
    {
        if (_heroRemote is { } remote)
        {
            MessageBar.IsOpen = false;
            await Services.Downloads.StartAsync(remote);
            return;
        }
        if (_hero is not { } entry || _applying) return;

        var efficient = AppSettings.Load(Services.Root).ModeFor(entry) == PlaybackMode.Efficient;
        var slow = efficient && entry.Fps > EfficientVariant.MinimumFps && entry.EfficientFile is null;
        _applying = true;
        HeroPrimary.IsEnabled = false;
        HeroProgress.Value = 0;
        HeroProgress.Visibility = Format.Show(slow);
        try
        {
            var progress = new Progress<double>(p => HeroProgress.Value = p * 100);
            await PlaybackService.ApplyAsync(entry, null, efficient, progress, CancellationToken.None);
            MessageBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or TranscodeException)
        {
            ShowMessage(InfoBarSeverity.Error, entry.Title + " could not be applied", ex.Message);
        }
        finally { _applying = false; }
        Rebuild();
    }

    private void OnHeroPreview(object sender, RoutedEventArgs e)
    {
        var source = _hero is { } entry
            ? LibraryManifest.Resolve(Services.Root, entry.File)
            : _heroRemote is { } remote ? remote.Preview720 ?? remote.Video : null;
        if (source is null) return;
        try { FullscreenPreview.Show(source); }
        catch (Exception ex) { ShowMessage(InfoBarSeverity.Error, "The preview could not start", ex.Message); }
    }

    private void OnHeroPick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryItemVm vm) return;
        try { AppSettings.Update(Services.Root, s => s.HeroId = vm.Entry.Id); }
        catch (IOException ex) { ShowMessage(InfoBarSeverity.Error, "That choice could not be saved", ex.Message); return; }
        Rebuild();
    }

    // ---- The rows

    private static CatalogItemVm? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as CatalogItemVm;

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        MessageBar.IsOpen = false;
        if (VmOf(sender) is { } vm) _ = Services.Downloads.StartAsync(vm.Entry);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) Services.Downloads.Cancel(vm.Entry.Id);
    }

    /// Full-screen preview: the local file when it is already downloaded, otherwise the short 720p loop.
    private void OnPreview(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        var local = Services.Library.Manifest.Find(vm.Entry.Id);
        var source = local is not null ? LibraryManifest.Resolve(Services.Root, local.File) : vm.Entry.Preview720 ?? vm.Entry.Video;
        try { FullscreenPreview.Show(source); }
        catch (Exception ex) { ShowMessage(InfoBarSeverity.Error, "The preview could not start", ex.Message); }
    }

    private void OnDownloadChanged(string id)
    {
        if (_vms.TryGetValue(id, out var vm)) vm.Refresh();
        if (_heroRemote?.Id == id) ShowHeroDownload(id);
    }

    private void OnDownloadFailed(CatalogEntry entry, string message) =>
        ShowMessage(InfoBarSeverity.Error, entry.Title + " could not be downloaded", message);

    private void OnRetry(object sender, RoutedEventArgs e) => _ = Services.Catalog.RefreshAsync(force: true);
}
