using Muro.Core.Catalog;
using Muro.Core.Library;

namespace Muro.Core.Tests;

public class InteractiveImportTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muro-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AFolder_OpensWithIndex_OrItsOnlyPage_ButNotAnAmbiguousOne()
    {
        var dir = TempDir();
        try
        {
            Assert.Null(InteractiveImport.FindPage(dir));
            File.WriteAllText(Path.Combine(dir, "main.html"), "<html/>");
            Assert.Equal("main.html", InteractiveImport.FindPage(dir));
            File.WriteAllText(Path.Combine(dir, "other.html"), "<html/>");
            Assert.Null(InteractiveImport.FindPage(dir));           // two pages, neither is index
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html/>");
            Assert.Equal("index.html", InteractiveImport.FindPage(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheTitle_IsThePagesOwn_ElseTheFileName()
    {
        var dir = TempDir();
        try
        {
            var titled = Path.Combine(dir, "a.html"); File.WriteAllText(titled, "<html><head><title> Cool Sky </title></head></html>");
            var plain = Path.Combine(dir, "plain-page.html"); File.WriteAllText(plain, "<html></html>");
            Assert.Equal("Cool Sky", InteractiveImport.TitleOf(titled));
            Assert.Equal("plain-page", InteractiveImport.TitleOf(plain));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ImportingAFolder_CopiesItsFiles_SkipsHiddenOnes_AndFilesItUnderMyVideos()
    {
        var root = TempDir(); var src = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(src, "index.html"), "<html><title>Rain</title></html>");
            Directory.CreateDirectory(Path.Combine(src, "img"));
            File.WriteAllText(Path.Combine(src, "img", "a.png"), "x");
            Directory.CreateDirectory(Path.Combine(src, ".git"));
            File.WriteAllText(Path.Combine(src, ".git", "config"), "secret");

            var entry = InteractiveImport.Import(root, src, [1, 2, 3], [4, 5]);

            Assert.True(entry.IsInteractive);
            Assert.True(entry.IsImported);                       // "My Videos": kept out of Explore
            Assert.Equal("Rain", entry.Title);
            var home = HostedInteractive.FolderFor(root, entry.Id);
            Assert.True(File.Exists(Path.Combine(home, "img", "a.png")));
            Assert.False(Directory.Exists(Path.Combine(home, ".git")));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(home, "still.jpg")));
            Assert.NotNull(LibraryManifest.Load(root).Find(entry.Id));
        }
        finally { Directory.Delete(root, true); Directory.Delete(src, true); }
    }

    [Fact]
    public void ImportingASinglePage_NamesItIndexHtml()
    {
        var root = TempDir(); var src = TempDir();
        try
        {
            var page = Path.Combine(src, "weird name.html"); File.WriteAllText(page, "<html/>");
            var entry = InteractiveImport.Import(root, page, [1], [2]);
            Assert.EndsWith("/index.html", entry.InteractivePage);
        }
        finally { Directory.Delete(root, true); Directory.Delete(src, true); }
    }

    [Fact]
    public void AFolderWithNoPage_IsRefused_AndLeavesNothingBehind()
    {
        var root = TempDir(); var src = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(src, "readme.txt"), "x");
            Assert.Throws<IOException>(() => InteractiveImport.Import(root, src, [1], [2]));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, HostedInteractive.FolderName)));
            Assert.Empty(LibraryManifest.Load(root).Wallpapers);
        }
        finally { Directory.Delete(root, true); Directory.Delete(src, true); }
    }
}
