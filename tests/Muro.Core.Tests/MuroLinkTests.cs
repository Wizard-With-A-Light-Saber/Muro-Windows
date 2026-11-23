using Muro.Core;

namespace Muro.Core.Tests;

public class MuroLinkTests
{
    [Fact]
    public void Set_ReadsTheIdAndDefaultsToTheDesktop()
    {
        var link = MuroLink.Parse("muro://set?id=abc-123");
        Assert.Equal(new MuroLink(MuroLinkKind.Set, "abc-123", null, "desktop"), link);
    }

    [Fact]
    public void Set_ReadsDisplayAndSurface_AndDecodesEscapes()
    {
        var link = MuroLink.Parse("muro://SET?id=a%20b&display=mon%2F1&surface=LockScreen");
        Assert.NotNull(link);
        Assert.Equal("a b", link!.Id);
        Assert.Equal("lockscreen", link.Surface);
        Assert.Equal("mon/1", link.Display);
    }

    [Theory]
    [InlineData("muro://pause", MuroLinkKind.Pause)]
    [InlineData("muro://resume", MuroLinkKind.Resume)]
    public void PauseAndResume_NeedNothingElse(string text, MuroLinkKind kind) =>
        Assert.Equal(kind, MuroLink.Parse(text)?.Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/set?id=a")]   // another scheme
    [InlineData("muro://set")]                     // no id
    [InlineData("muro://set?id=")]                 // empty id
    [InlineData("muro://set?id=a&surface=toaster")] // unknown surface
    [InlineData("muro://download?id=a")]           // a link never starts a download
    [InlineData("muro://")]
    public void Anything_Else_IsIgnored(string? text) => Assert.Null(MuroLink.Parse(text));
}
