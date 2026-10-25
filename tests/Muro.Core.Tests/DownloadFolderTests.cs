using Muro.Core.Library;

namespace Muro.Core.Tests;

public class DownloadFolderTests
{
    private static TempLibrary LibraryWithVideos()
    {
        var lib = new TempLibrary();
        lib.Write("Masters/a.mov", 2000);
        lib.Write("Masters/b.mov", 3000);
        lib.Write("Thumbnails/a.jpg", 10);
        return lib;
    }

    private static string NewFolder() => Directory.CreateTempSubdirectory("muro-drive").FullName;

    private static void Cleanup(string folder) { try { Directory.Delete(folder, true); } catch { } }

    [Fact]
    public void FreshLibrary_KeepsVideosInTheBuiltInFolder()
    {
        using var lib = LibraryWithVideos();
        Assert.IsType<DownloadFolder.Location.BuiltIn>(DownloadFolder.GetLocation(lib.Root));
    }

    [Fact]
    public void Move_PutsVideosInAMarkedFolder_AndEveryOldPathStillWorks()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            var steps = new List<(int, int)>();
            DownloadFolder.Move(lib.Root, drive, progress: (d, t) => steps.Add((d, t)));

            var target = Path.Combine(drive, DownloadFolder.FolderName);
            var location = Assert.IsType<DownloadFolder.Location.Custom>(DownloadFolder.GetLocation(lib.Root));
            Assert.Equal(target, location.Path, ignoreCase: true);
            Assert.Equal(2000, new FileInfo(Path.Combine(target, "a.mov")).Length);
            Assert.Equal(3000, new FileInfo(Path.Combine(target, "b.mov")).Length);
            Assert.True(File.Exists(Path.Combine(target, ".muro-wallpapers")));
            // The library still reads Masters/a.mov, which now resolves through the junction.
            Assert.Equal(2000, new FileInfo(lib.PathOf("Masters/a.mov")).Length);
            // Only videos move: thumbnails stay in the library.
            Assert.True(File.Exists(lib.PathOf("Thumbnails/a.jpg")));
            Assert.Equal((2, 2), steps[^1]);
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public void Move_BackToTheLibrary_RestoresARealFolderAndRemovesOurs()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            DownloadFolder.Move(lib.Root, drive);
            DownloadFolder.Move(lib.Root, null);

            Assert.IsType<DownloadFolder.Location.BuiltIn>(DownloadFolder.GetLocation(lib.Root));
            Assert.Equal(3000, new FileInfo(lib.PathOf("Masters/b.mov")).Length);
            Assert.False(Directory.Exists(Path.Combine(drive, DownloadFolder.FolderName)));
            Assert.Null(new DirectoryInfo(Path.Combine(lib.Root, "Masters")).LinkTarget);
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public void Move_BetweenTwoCustomFolders_CarriesTheVideosAndCleansTheOldFolder()
    {
        using var lib = LibraryWithVideos();
        var first = NewFolder();
        var second = NewFolder();
        try
        {
            DownloadFolder.Move(lib.Root, first);
            DownloadFolder.Move(lib.Root, second);

            Assert.Equal(Path.Combine(second, DownloadFolder.FolderName),
                Assert.IsType<DownloadFolder.Location.Custom>(DownloadFolder.GetLocation(lib.Root)).Path, ignoreCase: true);
            Assert.Equal(2000, new FileInfo(lib.PathOf("Masters/a.mov")).Length);
            Assert.False(Directory.Exists(Path.Combine(first, DownloadFolder.FolderName)));
        }
        finally { lib.Dispose(); Cleanup(first); Cleanup(second); }
    }

    [Fact]
    public void Move_RefusesAFolderInsideTheLibrary_ANonFolder_AndTheSamePlace()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            var inside = Path.Combine(lib.Root, "sub");
            Directory.CreateDirectory(inside);
            Assert.Equal(DownloadFolder.MoveError.InsideLibrary, Assert.Throws<DownloadFolder.MoveException>(() => DownloadFolder.Move(lib.Root, inside)).Error);
            Assert.Equal(DownloadFolder.MoveError.NotAFolder, Assert.Throws<DownloadFolder.MoveException>(() => DownloadFolder.Move(lib.Root, Path.Combine(drive, "nope"))).Error);

            DownloadFolder.Move(lib.Root, drive);
            Assert.Equal(DownloadFolder.MoveError.AlreadyThere, Assert.Throws<DownloadFolder.MoveException>(() => DownloadFolder.Move(lib.Root, drive)).Error);
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public void Move_RefusesAMuroWallpapersFolderThatHoldsSomeoneElsesFiles()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            // SweepOrphans deletes unreferenced files in Masters, so linking to a folder with other
            // content would delete a person files.
            var theirs = Path.Combine(drive, DownloadFolder.FolderName);
            Directory.CreateDirectory(theirs);
            File.WriteAllText(Path.Combine(theirs, "holiday.mp4"), "keep me");

            var ex = Assert.Throws<DownloadFolder.MoveException>(() => DownloadFolder.Move(lib.Root, drive));

            Assert.Equal(DownloadFolder.MoveError.FolderInUse, ex.Error);
            Assert.IsType<DownloadFolder.Location.BuiltIn>(DownloadFolder.GetLocation(lib.Root));
            Assert.True(File.Exists(Path.Combine(theirs, "holiday.mp4")));
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public void Move_Cancelled_ChangesNothingAndRemovesWhatItMade()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            var polls = 0;
            var ex = Assert.Throws<DownloadFolder.MoveException>(() =>
                DownloadFolder.Move(lib.Root, drive, isCancelled: () => polls++ >= 1));

            Assert.Equal(DownloadFolder.MoveError.Cancelled, ex.Error);
            Assert.IsType<DownloadFolder.Location.BuiltIn>(DownloadFolder.GetLocation(lib.Root));
            Assert.Equal(2000, new FileInfo(lib.PathOf("Masters/a.mov")).Length);
            Assert.Equal(3000, new FileInfo(lib.PathOf("Masters/b.mov")).Length);
            Assert.False(Directory.Exists(Path.Combine(drive, DownloadFolder.FolderName)));
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public async Task ADriveThatIsNotConnected_IsReportedAndRefusesNewDownloads()
    {
        using var lib = LibraryWithVideos();
        var drive = NewFolder();
        try
        {
            DownloadFolder.Move(lib.Root, drive);
            Directory.Delete(Path.Combine(drive, DownloadFolder.FolderName), recursive: true);   // unplugged

            Assert.IsType<DownloadFolder.Location.Unavailable>(DownloadFolder.GetLocation(lib.Root));
            Assert.Equal(DownloadFolder.MoveError.NotConnected, Assert.Throws<DownloadFolder.MoveException>(() => DownloadFolder.Move(lib.Root, null)).Error);

            var remote = new Muro.Core.Catalog.CatalogEntry { Id = "z", Video = "https://x/z.mov", Thumbnail = "https://x/z.jpg", SizeBytes = 1 };
            var ex = await Assert.ThrowsAsync<DownloadFolder.MoveException>(() =>
                Muro.Core.Catalog.Downloader.DownloadAsync(new HttpClient(), remote, lib.Root));
            Assert.Equal(DownloadFolder.MoveError.NotConnected, ex.Error);
        }
        finally { lib.Dispose(); Cleanup(drive); }
    }

    [Fact]
    public void IsInside_MatchesTheFolderItselfAndItsChildrenOnly()
    {
        Assert.True(DownloadFolder.IsInside(@"C:\a\b", @"C:\a"));
        Assert.True(DownloadFolder.IsInside(@"C:\a", @"C:\a\"));
        Assert.False(DownloadFolder.IsInside(@"C:\ab", @"C:\a"));
    }
}
