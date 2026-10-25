using Muro.Core;
using Muro.Core.Library;

namespace Muro.Core.Tests;

/// A throwaway library root. Disposing removes a Masters junction first so a cleanup can never
/// follow the link into a folder that belongs to another test.
public sealed class TempLibrary : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("muro-lib").FullName;

    public string Write(string relative, int bytes = 10, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modifiedUtc is { } t) File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static WallpaperEntry Entry(string id, string title = "T", string category = "Nature") => new()
    {
        Id = id, Title = title, Category = category, File = "Masters/" + id + ".mov", Thumbnail = "Thumbnails/" + id + ".jpg",
        Width = 1920, Height = 1080, Fps = 30, Duration = 10, SizeBytes = 10,
    };

    public void Dispose()
    {
        try
        {
            var masters = Path.Combine(Root, "Masters");
            if (new DirectoryInfo(masters).LinkTarget is not null) Directory.Delete(masters, recursive: false);
            Directory.Delete(Root, recursive: true);
        }
        catch { }
    }
}

public class LibraryTests
{
    [Fact]
    public void Manifest_ReadsTheMacOSShape()
    {
        using var lib = new TempLibrary();
        File.WriteAllText(LibraryManifest.PathIn(lib.Root), """
            {"wallpapers":[{"category":"Nature","dateAdded":"2026-10-23T03:06:47Z","duration":30,"efficientFile":"Masters/x-eff.mov",
            "file":"Masters/x.mov","fps":30,"height":2304,"id":"x","liked":true,"previewFile":"Previews/x-p720.mov","sizeBytes":77612352,
            "sourceFingerprint":"abc","thumbnail":"Thumbnails/x.jpg","title":"Misty","width":4096,"pauseAfterSeconds":60}]}
            """);
        var state = LibraryManifest.Read(lib.Root);
        Assert.Equal(LibraryManifest.Status.Loaded, state.Status);
        var w = Assert.Single(state.Manifest!.Wallpapers);
        Assert.Equal(("x", "Misty", "Nature", 4096, 2304), (w.Id, w.Title, w.Category, w.Width, w.Height));
        Assert.True(w.Liked);
        Assert.Equal(60, w.PauseAfterSeconds);
        Assert.Equal(new DateTime(2026, 10, 23, 3, 6, 47, DateTimeKind.Utc), w.DateAdded);
        Assert.Equal(["Masters/x.mov", "Masters/x-eff.mov", "Previews/x-p720.mov", "Thumbnails/x.jpg"], w.RelativeFiles);
    }

    [Fact]
    public void Manifest_WritesDatesToTheSecond_AndRoundTrips()
    {
        using var lib = new TempLibrary();
        var entry = TempLibrary.Entry("a");
        entry.DateAdded = new DateTime(2026, 10, 23, 3, 6, 47, 912, DateTimeKind.Utc);
        new LibraryManifest { Wallpapers = { entry } }.Save(lib.Root);
        Assert.Contains("\"dateAdded\": \"2026-10-23T03:06:47Z\"", File.ReadAllText(LibraryManifest.PathIn(lib.Root)));
        Assert.Equal("a", LibraryManifest.Load(lib.Root).Wallpapers.Single().Id);
    }

    [Fact]
    public void Manifest_TellsMissingFromDamaged()
    {
        using var lib = new TempLibrary();
        Assert.Equal(LibraryManifest.Status.Missing, LibraryManifest.Read(lib.Root).Status);
        File.WriteAllText(LibraryManifest.PathIn(lib.Root), "{ not json");
        Assert.Equal(LibraryManifest.Status.Damaged, LibraryManifest.Read(lib.Root).Status);
        Assert.Empty(LibraryManifest.Load(lib.Root).Wallpapers);
    }

    [Fact]
    public void Resolve_KeepsAbsolutePaths_AndRootsRelativeOnes()
    {
        Assert.Equal(@"C:\Videos\a.mov", LibraryManifest.Resolve(@"D:\lib", @"C:\Videos\a.mov"));
        Assert.Equal(@"D:\lib\Masters\a.mov", LibraryManifest.Resolve(@"D:\lib", "Masters/a.mov"));
    }

    [Fact]
    public void Writer_NeverOverwritesADamagedManifest()
    {
        using var lib = new TempLibrary();
        File.WriteAllText(LibraryManifest.PathIn(lib.Root), "{ not json");
        Assert.Throws<ManifestUnreadableException>(() => LibraryWriter.Update(lib.Root, m => m.Wallpapers.Add(TempLibrary.Entry("a"))));
        Assert.Equal("{ not json", File.ReadAllText(LibraryManifest.PathIn(lib.Root)));
    }

    [Fact]
    public void Writer_ConcurrentUpdates_LoseNothing()
    {
        using var lib = new TempLibrary();
        Parallel.For(0, 40, i => LibraryWriter.Update(lib.Root, m => m.Wallpapers.Add(TempLibrary.Entry("w" + i))));
        Assert.Equal(40, LibraryManifest.Load(lib.Root).Wallpapers.Select(w => w.Id).Distinct().Count());
    }

    [Fact]
    public void Delete_RemovesEntryAndEveryFile_IncludingAVariantAddedAfterTheCallerLooked()
    {
        using var lib = new TempLibrary();
        foreach (var f in new[] { "Masters/a.mov", "Thumbnails/a.jpg", "Masters/b.mov", "Thumbnails/b.jpg", "Masters/a-eff.mov" }) lib.Write(f);
        LibraryWriter.Update(lib.Root, m => { m.Wallpapers.Add(TempLibrary.Entry("a")); m.Wallpapers.Add(TempLibrary.Entry("b")); });
        // The variant is generated after whoever asks for the delete read the library.
        LibraryWriter.Update(lib.Root, m => m.Find("a")!.EfficientFile = "Masters/a-eff.mov");

        var manifest = LibraryWriter.Delete(lib.Root, new HashSet<string> { "a" });

        Assert.Equal(["b"], manifest.Wallpapers.Select(w => w.Id));
        Assert.False(File.Exists(lib.PathOf("Masters/a.mov")));
        Assert.False(File.Exists(lib.PathOf("Masters/a-eff.mov")));
        Assert.False(File.Exists(lib.PathOf("Thumbnails/a.jpg")));
        Assert.True(File.Exists(lib.PathOf("Masters/b.mov")));
    }

    [Fact]
    public void Deletion_TakesTheWallpaperOffTheDesktopFirst_AndRunsTheSinks()
    {
        using var lib = new TempLibrary();
        lib.Write("Masters/a.mov"); lib.Write("Masters/b.mov");
        LibraryWriter.Update(lib.Root, m => { m.Wallpapers.Add(TempLibrary.Entry("a")); m.Wallpapers.Add(TempLibrary.Entry("b")); });
        var config = new EngineConfig { AllDisplays = new("a") };
        config.PerDisplay["mon1"] = new("a");
        config.PerDisplay["mon2"] = new("b");
        config.Save(lib.Root);

        IReadOnlySet<string>? seen = null;
        var result = WallpaperDeletion.Delete(lib.Root, new HashSet<string> { "a" }, [ids => seen = ids]);

        Assert.True(result.ClearedDesktop);
        var after = EngineConfig.Load(lib.Root);
        Assert.Null(after.AllDisplays);
        Assert.Equal(new[] { "mon2" }, after.PerDisplay.Keys.ToArray());
        Assert.Equal(new[] { "a" }, seen!.ToArray());
    }

    [Fact]
    public void Sweep_RemovesOldOrphans_ButSparesReferencedFreshAndHiddenFiles()
    {
        using var lib = new TempLibrary();
        var old = DateTime.UtcNow.AddHours(-2);
        lib.Write("Masters/kept.mov", modifiedUtc: old);
        lib.Write("Masters/orphan.mov", 100, old);
        lib.Write("Masters/fresh.mov", 100);                           // an import may still be writing it
        lib.Write("Masters/.partial.mov.partial", 100, old);           // a download in flight
        LibraryWriter.Update(lib.Root, m => m.Wallpapers.Add(new WallpaperEntry { Id = "k", File = "Masters/kept.mov" }));

        var freed = LibraryWriter.SweepOrphans(lib.Root);

        Assert.Equal(100, freed);
        Assert.False(File.Exists(lib.PathOf("Masters/orphan.mov")));
        Assert.True(File.Exists(lib.PathOf("Masters/kept.mov")));
        Assert.True(File.Exists(lib.PathOf("Masters/fresh.mov")));
        Assert.True(File.Exists(lib.PathOf("Masters/.partial.mov.partial")));
    }

    [Fact]
    public void Sweep_DoesNothingWhenTheManifestCannotBeRead()
    {
        using var lib = new TempLibrary();
        lib.Write("Masters/a.mov", modifiedUtc: DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(LibraryManifest.PathIn(lib.Root), "{ not json");
        Assert.Equal(0, LibraryWriter.SweepOrphans(lib.Root));
        Assert.True(File.Exists(lib.PathOf("Masters/a.mov")));
    }

    [Fact]
    public void Usage_ReadsRealFileSizes_AndCountsAMissingFileAsZero()
    {
        using var lib = new TempLibrary();
        lib.Write("Masters/a.mov", 1000); lib.Write("Thumbnails/a.jpg", 24);
        var entry = TempLibrary.Entry("a");
        entry.EfficientFile = "Masters/a-eff.mov";   // not on disk
        Assert.Equal(1024, LibraryUsage.BytesOf(lib.Root, entry));
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameContent_AndChangesWithEitherEnd()
    {
        using var lib = new TempLibrary();
        var data = new byte[3 << 20];
        new Random(1).NextBytes(data);
        var a = Path.Combine(lib.Root, "a.bin"); File.WriteAllBytes(a, data);
        var copy = Path.Combine(lib.Root, "copy.bin"); File.Copy(a, copy);
        var head = (byte[])data.Clone(); head[10] ^= 1;
        var tail = (byte[])data.Clone(); tail[^10] ^= 1;
        File.WriteAllBytes(Path.Combine(lib.Root, "head.bin"), head);
        File.WriteAllBytes(Path.Combine(lib.Root, "tail.bin"), tail);

        var fp = Fingerprint.Of(a);
        Assert.NotNull(fp);
        Assert.Equal(64, fp!.Length);
        Assert.Equal(fp, Fingerprint.Of(copy));
        Assert.NotEqual(fp, Fingerprint.Of(Path.Combine(lib.Root, "head.bin")));
        Assert.NotEqual(fp, Fingerprint.Of(Path.Combine(lib.Root, "tail.bin")));
        Assert.Null(Fingerprint.Of(Path.Combine(lib.Root, "missing.bin")));
    }

    [Fact]
    public void Fingerprint_WorksOnFilesSmallerThanTheChunk()
    {
        using var lib = new TempLibrary();
        var path = lib.Write("tiny.bin", 100);
        Assert.NotNull(Fingerprint.Of(path));
        File.WriteAllBytes(path, []);
        Assert.NotNull(Fingerprint.Of(path));
    }
}
