using Muro.Core;

namespace Muro.Core.Tests;

public class EngineConfigTests
{
    private static string Temp() => Directory.CreateTempSubdirectory("muro-test").FullName;

    [Fact] public void MissingFile_GivesDefaults() => Assert.True(EngineConfig.Load(Temp()).PauseOnFullscreen);

    [Fact] public void DamagedFile_GivesDefaults()
    {
        var root = Temp();
        File.WriteAllText(EngineConfig.PathIn(root), "{ not json");
        Assert.Null(EngineConfig.Load(root).Paused);
    }

    [Fact] public void RoundTrip_KeepsAssignments()
    {
        var root = Temp();
        var cfg = new EngineConfig { AllDisplays = new("a", "efficient"), PlayOnlyOnDesktop = true };
        cfg.PerDisplay["mon2"] = new("b");
        cfg.Save(root);
        var back = EngineConfig.Load(root);
        Assert.Equal("efficient", back.AssignmentFor("unknown")!.Mode);
        Assert.Equal("b", back.AssignmentFor("mon2")!.WallpaperID);
        Assert.True(back.PlayOnlyOnDesktop);
    }
}
