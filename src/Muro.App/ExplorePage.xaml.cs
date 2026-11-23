using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core;
using Muro.Core.Catalog;
using Muro.Core.Library;

namespace Muro.App;

public sealed partial class ExplorePage : Page
{
    private static readonly string AllCategories = Loc.T("All categories");   // read once the language is set
    private readonly Dictionary<string, CatalogItemVm> _vms = [];
    private bool _updatingCategories;

    public ExplorePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.Catalog.Changed += Rebuild;
        Services.Library.Changed += RefreshStates;
        Services.Downloads.Changed += OnDownloadChanged;
        Services.Downloads.Failed += OnDownloadFailed;
        Rebuild();
        _ = Services.Catalog.RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Services.Catalog.Changed -= Rebuild;
        Services.Library.Changed -= RefreshStates;
        Services.Downloads.Changed -= OnDownloadChanged;
        Services.Downloads.Failed -= OnDownloadFailed;
    }

    private void Rebuild()
    {
        var catalog = Services.Catalog;
        Loading.IsActive = !catalog.Loaded;

        // A blocked connection must never read as "nothing matches": say what is wrong.
        if (catalog.Error is { } error)
        {
            ErrorBar.Title = error.Title;
            ErrorBar.Message = error.Detail;
            ErrorBar.IsOpen = true;
        }
        else ErrorBar.IsOpen = false;

        _updatingCategories = true;
        var selected = CategoryBox.SelectedItem as string ?? AllCategories;
        var categories = new List<string> { AllCategories };
        categories.AddRange(catalog.Items.Select(i => i.Category).Distinct().OrderBy(c => c));
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedItem = categories.Contains(selected) ? selected : AllCategories;
        _updatingCategories = false;

        foreach (var entry in catalog.Items)
            if (!_vms.ContainsKey(entry.Id)) _vms[entry.Id] = new CatalogItemVm(entry);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var category = CategoryBox.SelectedItem as string;
        var shown = Services.Catalog.Items
            .Where(i => (category is null || category == AllCategories || i.Category == category)
                        && (query.Length == 0 || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(i => _vms[i.Id])
            .ToList();
        foreach (var vm in shown) vm.Refresh();
        // Newly arrived first, then newest published, then by title.
        Grid.ItemsSource = shown
            .OrderByDescending(v => v.IsNew)
            .ThenByDescending(v => v.Entry.PublishedAt ?? DateTime.MinValue)
            .ThenBy(v => v.Title)
            .ToList();
        CountText.Text = shown.Count + " of " + Services.Catalog.Items.Count;
    }

    private void RefreshStates()
    {
        foreach (var vm in _vms.Values) vm.Refresh();
    }

    private void OnDownloadChanged(string id)
    {
        if (_vms.TryGetValue(id, out var vm)) vm.Refresh();
    }

    private void OnDownloadFailed(CatalogEntry entry, string message)
    {
        DownloadBar.Title = entry.Title + " could not be downloaded";
        DownloadBar.Message = message;
        DownloadBar.IsOpen = true;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingCategories) ApplyFilter();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = Services.Catalog.RefreshAsync(force: true);

    private static CatalogItemVm? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as CatalogItemVm;

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        DownloadBar.IsOpen = false;
        if (VmOf(sender) is { } vm) _ = Services.Downloads.StartAsync(vm.Entry);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) Services.Downloads.Cancel(vm.Entry.Id);
    }

    /// Full-screen preview. Plays the local file when it is already downloaded, otherwise streams the
    /// short 720p loop (or the video itself on older catalogs without one).
    private void OnPreview(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        var local = Services.Library.Manifest.Find(vm.Entry.Id);
        var source = local is not null
            ? LibraryManifest.Resolve(Services.Root, local.File)
            : vm.Entry.PreviewSource;
        try { FullscreenPreview.Show(source); }
        catch (Exception ex)
        {
            DownloadBar.Title = "The preview could not start";
            DownloadBar.Message = ex.Message;
            DownloadBar.IsOpen = true;
        }
    }
}
