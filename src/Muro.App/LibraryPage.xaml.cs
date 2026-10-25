using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muro.Core;
using Muro.Core.Library;
using Muro.Core.Media;
using Muro.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Muro.App;

public sealed partial class LibraryPage : Page
{
    private CancellationTokenSource? _busy;

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Services.Library.Changed += Reload; Reload(); };
        Unloaded += (_, _) => Services.Library.Changed -= Reload;
    }

    // ---- Showing the library

    private void Reload()
    {
        Services.Library.Reload(raise: false);
        var manifest = Services.Library.Manifest;
        var playing = PlaybackService.PlayingIds();
        var items = manifest.Wallpapers
            .OrderByDescending(w => w.DateAdded)
            .Select(w => new LibraryItemVm(w, LibraryUsage.BytesOf(Services.Root, w), playing.Contains(w.Id)))
            .ToList();
        Grid.ItemsSource = items;
        EmptyText.Visibility = Format.Show(items.Count == 0 && !Services.Library.Damaged);
        SummaryText.Text = items.Count + (items.Count == 1 ? " wallpaper" : " wallpapers") + "  -  " + Format.Bytes(items.Sum(i => i.Bytes));

        if (Services.Library.Damaged)
            ShowMessage(InfoBarSeverity.Error, "Muro could not read your wallpaper list",
                "Your wallpaper files are untouched, and Muro will not change the list until it can be read. The list is library.json in " + Services.Root + ".");

        switch (DownloadFolder.GetLocation(Services.Root))
        {
            case DownloadFolder.Location.Custom custom:
                FolderText.Text = "Wallpapers are kept in " + custom.Path;
                ResetFolderButton.Visibility = Visibility.Visible;
                break;
            case DownloadFolder.Location.Unavailable gone:
                FolderText.Text = "Wallpapers are kept in " + gone.Path + ", which is not connected. Connect it and they come back by themselves.";
                ResetFolderButton.Visibility = Visibility.Visible;
                break;
            default:
                FolderText.Text = "Wallpapers are kept in the Muro folder on this PC.";
                ResetFolderButton.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    private static LibraryItemVm? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as LibraryItemVm;

    private void OnPreview(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        try { FullscreenPreview.Show(LibraryManifest.Resolve(Services.Root, vm.Entry.File)); }
        catch (Exception ex) { ShowMessage(InfoBarSeverity.Error, "The preview could not start", ex.Message); }
    }

    // ---- Playing a wallpaper

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        var flyout = new MenuFlyout();
        AddApplyItems(flyout.Items, vm, null, "All displays");
        var monitors = Monitors.All();
        if (monitors.Count > 1)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            for (var i = 0; i < monitors.Count; i++)
            {
                var sub = new MenuFlyoutSubItem { Text = "Display " + (i + 1) + "  (" + monitors[i].Bounds.Width + "x" + monitors[i].Bounds.Height + ")" };
                AddApplyItems(sub.Items, vm, i, null);
                flyout.Items.Add(sub);
            }
        }
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void AddApplyItems(IList<MenuFlyoutItemBase> items, LibraryItemVm vm, int? monitor, string? label)
    {
        var smooth = new MenuFlyoutItem { Text = label is null ? "Smooth (original frame rate)" : label + " - Smooth" };
        smooth.Click += (_, _) => _ = ApplyAsync(vm, monitor, efficient: false);
        var efficient = new MenuFlyoutItem { Text = label is null ? "Efficient (30 fps)" : label + " - Efficient (30 fps)" };
        efficient.Click += (_, _) => _ = ApplyAsync(vm, monitor, efficient: true);
        items.Add(smooth);
        items.Add(efficient);
    }

    private async Task ApplyAsync(LibraryItemVm vm, int? monitor, bool efficient)
    {
        var needsVariant = efficient && vm.Entry.Fps > EfficientVariant.MinimumFps && vm.Entry.EfficientFile is null;
        using var cts = new CancellationTokenSource();
        try
        {
            if (needsVariant) BeginBusy("Making the 30 fps version of " + vm.Title, cts);
            var progress = new Progress<double>(p => BusyProgress.Value = p * 100);
            await PlaybackService.ApplyAsync(vm.Entry, monitor, efficient, progress, cts.Token);
            MessageBar.IsOpen = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or TranscodeException)
        {
            ShowMessage(InfoBarSeverity.Error, vm.Title + " could not be applied", ex.Message);
        }
        finally { EndBusy(); }
        Reload();
    }

    // ---- Pause after, per wallpaper

    private static readonly (string Label, int? Seconds)[] PauseChoices =
    [
        ("Follow the Schedules setting", null), ("Never pause this wallpaper", 0), ("After 10 seconds", 10), ("After 30 seconds", 30),
        ("After 1 minute", 60), ("After 5 minutes", 300), ("After 15 minutes", 900), ("After 1 hour", 3600),
    ];

    /// A value of its own wins over the global setting, and zero means never, which is a decision and not
    /// the same as having no value.
    private void OnPauseMenu(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        var flyout = new MenuFlyout();
        foreach (var (label, seconds) in PauseChoices)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = vm.Entry.PauseAfterSeconds == seconds };
            item.Click += (_, _) =>
            {
                if (Services.Library.Damaged) return;
                LibraryWriter.Update(Services.Root, m => { if (m.Find(vm.Entry.Id) is { } entry) entry.PauseAfterSeconds = seconds; });
                Reload();
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt((FrameworkElement)sender);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        PlaybackService.StopAll();
        Reload();
    }

    // ---- Deleting

    private void OnSelectToggle(object sender, RoutedEventArgs e)
    {
        Grid.SelectionMode = SelectToggle.IsChecked == true ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        DeleteSelectedButton.Visibility = Format.Show(SelectToggle.IsChecked == true);
        OnSelectionChanged(sender, null!);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = Grid.SelectedItems.Count;
        DeleteSelectedButton.Content = count == 0 ? "Delete" : "Delete (" + count + ")";
        DeleteSelectedButton.IsEnabled = count > 0;
    }

    private void OnDeleteOne(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _ = ConfirmAndDeleteAsync([vm]);
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e) =>
        _ = ConfirmAndDeleteAsync(Grid.SelectedItems.OfType<LibraryItemVm>().ToList());

    /// Nothing is deleted without an answer. Deleting also takes the wallpaper off the desktop first and
    /// removes it from everything that points at it, which WallpaperDeletion does in that order.
    private async Task ConfirmAndDeleteAsync(List<LibraryItemVm> items)
    {
        if (items.Count == 0 || Services.Library.Damaged) return;
        var bytes = items.Sum(i => i.Bytes);
        var what = items.Count == 1 ? "\"" + items[0].Title + "\"" : items.Count + " wallpapers";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete " + what + "?",
            Content = "This removes " + (items.Count == 1 ? "it" : "them") + " from this PC and frees " + Format.Bytes(bytes)
                      + ". If " + (items.Count == 1 ? "it is" : "any are") + " playing, the wallpaper is taken off the desktop. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var ids = items.Select(i => i.Entry.Id).ToHashSet();
        try
        {
            // The delete retries for a couple of seconds while the engine lets go of a playing file.
            await Task.Run(() => WallpaperDeletion.Delete(Services.Root, ids));
        }
        catch (IOException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Muro could not delete", ex.Message);
        }
        SelectToggle.IsChecked = false;
        OnSelectToggle(SelectToggle, null!);
        Reload();
    }

    // ---- Importing

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Import into Muro";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var dropped = await e.DataView.GetStorageItemsAsync();
        await ImportAsync(dropped.OfType<StorageFile>().Select(f => f.Path).ToList(), dropped.Count);
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, MainWindow.Instance!.Handle);
        foreach (var extension in Importer.Extensions) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ImportAsync(files.Select(f => f.Path).ToList(), files.Count);
    }

    private async Task ImportAsync(List<string> paths, int offered)
    {
        if (Services.Library.Damaged) return;
        var videos = paths.Where(Importer.IsVideo).ToList();
        var skipped = offered - videos.Count;
        if (videos.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "Nothing was added", "Muro imports MP4, MOV, M4V, MKV, WebM and AVI videos.");
            return;
        }
        var ffmpeg = Ffmpeg.Locate();
        if (ffmpeg is null)
        {
            ShowMessage(InfoBarSeverity.Error, "Muro cannot convert videos", "The bundled video converter (ffmpeg) is missing, so nothing can be imported.");
            return;
        }

        using var cts = new CancellationTokenSource();
        var already = new List<string>();
        var failures = new List<string>();
        var done = 0;
        try
        {
            for (var i = 0; i < videos.Count; i++)
            {
                var label = videos.Count == 1 ? "Importing " : "Importing " + (i + 1) + " of " + videos.Count + ": ";
                BeginBusy(label + Path.GetFileName(videos[i]), cts);
                try
                {
                    var progress = new Progress<double>(p => BusyProgress.Value = p * 100);
                    var result = await Importer.ImportAsync(ffmpeg, Services.Root, videos[i], progress: progress, ct: cts.Token);
                    if (result.Duplicate) already.Add(result.Entry.Title); else done++;
                    Reload();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is TranscodeException or IOException)
                {
                    failures.Add(Path.GetFileName(videos[i]) + ": " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { EndBusy(); }

        var notes = new List<string>();
        if (done > 0) notes.Add(done + (done == 1 ? " video imported." : " videos imported."));
        if (already.Count > 0) notes.Add(already.Count == 1 ? already[0] + " is already in your library." : already.Count + " were already in your library.");
        if (skipped > 0) notes.Add(skipped + (skipped == 1 ? " file was skipped (not a video)." : " files were skipped (not videos)."));
        notes.AddRange(failures);
        if (notes.Count > 0)
            ShowMessage(failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, failures.Count > 0 ? "Some videos could not be imported" : "Done", string.Join("  ", notes));
    }

    // ---- Busy bar (imports, 30 fps copies)

    private void BeginBusy(string text, CancellationTokenSource cts)
    {
        _busy = cts;
        BusyBar.Title = text;
        BusyProgress.Value = 0;
        BusyBar.IsOpen = true;
    }

    private void EndBusy()
    {
        _busy = null;
        BusyBar.IsOpen = false;
    }

    private void OnBusyCancel(object sender, RoutedEventArgs e) => _busy?.Cancel();

    // ---- Download folder

    private async void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, MainWindow.Instance!.Handle);
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFolderAsync() is { } folder) await MoveFolderAsync(folder.Path);
    }

    private async void OnResetFolder(object sender, RoutedEventArgs e) => await MoveFolderAsync(null);

    /// Moves the videos with a progress dialog that can be cancelled. Cancelling changes nothing.
    private async Task MoveFolderAsync(string? target)
    {
        if (Services.Library.Damaged) return;
        var bar = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 12, 0, 0) };
        var text = new TextBlock { Text = "Preparing..." };
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(bar);
        using var cts = new CancellationTokenSource();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Moving your wallpapers", Content = panel, CloseButtonText = "Cancel" };
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        var shown = dialog.ShowAsync();

        var queue = DispatcherQueue;
        try
        {
            await DownloadFolderMove.MoveAsync(Services.Root, target,
                progress: (done, total) => queue.TryEnqueue(() => text.Text = "Moved " + done + " of " + total + " videos"),
                bytes: (copied, total) => queue.TryEnqueue(() => bar.Value = total == 0 ? 100 : copied * 100.0 / total),
                ct: cts.Token);
            MessageBar.IsOpen = false;
        }
        catch (Exception ex) when (ex is DownloadFolder.MoveException or IOException or OperationCanceledException)
        {
            ShowMessage(InfoBarSeverity.Warning, "Your wallpapers were not moved", ex.Message);
        }
        finally { dialog.Hide(); }
        await shown;
        Reload();
    }
}
