using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Muro.Core;
using Muro.Core.Catalog;
using Muro.Core.Library;

namespace Muro.App;

/// One card in Explore. Thumbnails are created lazily, so only cards that scroll into view cost memory.
internal sealed class CatalogItemVm(CatalogEntry entry) : INotifyPropertyChanged
{
    private BitmapImage? _thumb;

    public CatalogEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Category => Entry.Category;
    public string Meta => (Entry.Interactive is not null ? Loc.T("Interactive") : Format.Resolution(Entry.Width, Entry.Height, Entry.Fps)) + "  " + Format.Bytes(Entry.SizeBytes);
    public BitmapImage Thumb => _thumb ??= new BitmapImage { DecodePixelWidth = 560, UriSource = new Uri(Entry.Thumbnail) };

    public bool IsNew { get; set; }
    public bool InLibrary { get; set; }
    public bool Downloading { get; set; }
    public double Progress { get; set; }

    public Visibility NewVisibility => Format.Show(IsNew && !InLibrary);
    public Visibility DownloadVisibility => Format.Show(!InLibrary && !Downloading);
    public Visibility ProgressVisibility => Format.Show(Downloading);
    public Visibility OwnedVisibility => Format.Show(InLibrary && !Downloading);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Re-reads everything bound to this card from the services.
    public void Refresh()
    {
        InLibrary = Services.Library.Ids.Contains(Entry.Id);
        Downloading = Services.Downloads.IsActive(Entry.Id);
        Progress = Services.Downloads.ProgressOf(Entry.Id) * 100;
        IsNew = Services.Catalog.NewIds.Contains(Entry.Id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// One card in Library.
internal sealed class LibraryItemVm(WallpaperEntry entry, long bytes, bool playing) : INotifyPropertyChanged
{
    private BitmapImage? _thumb;

    public WallpaperEntry Entry { get; } = entry;
    public long Bytes { get; } = bytes;
    public string Title => Entry.Title;
    public string Subtitle => Entry.Category + (Entry.IsInteractive ? "  -  " + Loc.T("Interactive") : "  -  " + Format.Resolution(Entry.Width, Entry.Height, Entry.Fps));
    public string SizeText => Format.Bytes(Bytes) + (Entry.EfficientFile is null ? "" : "  (includes 30 fps copy)");
    public bool IsPlaying { get; set; } = playing;
    public Visibility PlayingVisibility => Format.Show(IsPlaying);

    public BitmapImage? Thumb
    {
        get
        {
            if (_thumb is not null || string.IsNullOrEmpty(Entry.Thumbnail)) return _thumb;
            var path = LibraryManifest.Resolve(Services.Root, Entry.Thumbnail);
            return File.Exists(path) ? _thumb = new BitmapImage { DecodePixelWidth = 560, UriSource = new Uri(path) } : null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
