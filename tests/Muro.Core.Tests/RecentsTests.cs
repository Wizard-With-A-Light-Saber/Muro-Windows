using Muro.Core;

namespace Muro.Core.Tests;

public class RecentsTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "muro-recents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void NoteRecent_PutsTheNewestFirst_MovesARepeatUp_AndKeepsTen()
    {
        var root = TempRoot();
        try
        {
            for (var i = 1; i <= 12; i++) AppSettings.NoteRecent(root, [$"w{i}"]);
            var list = AppSettings.Load(root).Recents;
            Assert.Equal(AppSettings.RecentLimit, list.Count);
            Assert.Equal("w12", list[0]);
            Assert.Equal("w3", list[^1]);

            AppSettings.NoteRecent(root, ["w5"]);
            Assert.Equal(["w5", "w12", "w11"], AppSettings.Load(root).Recents.Take(3));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NoteRecent_WithNothing_WritesNoFile()
    {
        var root = TempRoot();
        try
        {
            AppSettings.NoteRecent(root, []);
            Assert.False(File.Exists(AppSettings.PathIn(root)));
        }
        finally { Directory.Delete(root, true); }
    }
}
