namespace Muro.Core.Media;

/// Colour looks that are baked into a wallpaper's screen-sized copy once, with ffmpeg, so playing them costs nothing
/// extra at run time (a live shader costs about 15% GPU). Each one is a plain ffmpeg filter, so they work on any
/// ffmpeg build Muro ships with.
public static class Looks
{
    public static readonly IReadOnlyList<(string Name, string Label, string Filter)> All =
    [
        ("warm", "Warm", "colorchannelmixer=rr=1.06:bb=0.92"),
        ("cool", "Cool", "colorchannelmixer=rr=0.93:bb=1.07"),
        ("mono", "Black and white", "hue=s=0"),
        ("vivid", "Vivid", "hue=s=1.35"),
        ("dim", "Dim", "colorchannelmixer=rr=0.85:gg=0.85:bb=0.85"),
        ("night", "Night", "colorchannelmixer=rr=0.78:gg=0.82:bb=0.95"),
    ];

    /// The ffmpeg filter for a look name; null for none or a name this version does not know (a library.json from a
    /// newer Muro), which plays the plain wallpaper instead of failing.
    public static string? Filter(string? name) => All.FirstOrDefault(l => l.Name == name).Filter;
}
