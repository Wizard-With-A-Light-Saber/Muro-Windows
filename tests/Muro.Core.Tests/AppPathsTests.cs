using Muro.Core;

namespace Muro.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Root_IsUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, AppPaths.Root);
        Assert.EndsWith("Muro", AppPaths.Root);
    }
}
