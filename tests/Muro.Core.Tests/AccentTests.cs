using Muro.Core.Accent;

namespace Muro.Core.Tests;

public class AccentTests
{
    private static byte[] Picture(params (Rgb Color, int Count)[] parts)
    {
        var bytes = new List<byte>();
        foreach (var (c, n) in parts) for (var i = 0; i < n; i++) { bytes.Add(c.R); bytes.Add(c.G); bytes.Add(c.B); }
        return bytes.ToArray();
    }

    private static double Hue(Rgb c) => c.ToHsl().H;

    [Fact]
    public void ADarkPictureWithAGlowingColour_GivesThatColour_NotBlack()
    {
        // Mostly near black, a small orange light.
        var accent = AccentPicker.Pick(Picture((new Rgb(10, 8, 8), 900), (new Rgb(240, 130, 20), 100)));
        Assert.InRange(Hue(accent), 20, 40);          // orange
        Assert.InRange(accent.ToHsl().L, 0.38, 0.55); // mid-light, so white text reads on it
        Assert.True(accent.ToHsl().S >= 0.55);        // vivid
    }

    [Fact]
    public void TheLargerColourWins()
    {
        var blueHeavy = AccentPicker.Pick(Picture((new Rgb(30, 90, 220), 700), (new Rgb(220, 40, 40), 300)));
        Assert.InRange(Hue(blueHeavy), 205, 240);
        var redHeavy = AccentPicker.Pick(Picture((new Rgb(30, 90, 220), 300), (new Rgb(220, 40, 40), 700)));
        Assert.True(Hue(redHeavy) is < 15 or > 345);
    }

    [Fact]
    public void GreyBlackAndWhiteCountForNothing()
    {
        var accent = AccentPicker.Pick(Picture((new Rgb(128, 128, 128), 500), (new Rgb(0, 0, 0), 300), (new Rgb(255, 255, 255), 150), (new Rgb(20, 160, 60), 50)));
        Assert.InRange(Hue(accent), 120, 150);   // the little bit of green, not grey
    }

    [Fact]
    public void ANoColourPicture_GivesAGrey_AndAnEmptyOneWindowsBlue()
    {
        var grey = AccentPicker.Pick(Picture((new Rgb(100, 100, 100), 400)));
        Assert.Equal((grey.R, grey.R), (grey.G, grey.B));
        Assert.Equal(new Rgb(0x00, 0x78, 0xD4), AccentPicker.Pick([]));
    }

    [Fact]
    public void Palette_HasEightShades_LightToDark_WithTheAccentInTheMiddle()
    {
        var accent = new Rgb(0x00, 0x78, 0xD4);
        var palette = AccentPicker.Palette(accent);
        Assert.Equal(8, palette.Length);
        Assert.Equal(accent, palette[3]);
        var lightness = palette.Select(p => p.ToHsl().L).ToArray();
        Assert.True(lightness.Zip(lightness.Skip(1), (a, b) => a > b).All(x => x), "lightest first, darkest last");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(200, 40, 120)]
    [InlineData(12, 200, 90)]
    public void Hsl_RoundTrips(byte r, byte g, byte b)
    {
        var (h, s, l) = new Rgb(r, g, b).ToHsl();
        var back = Rgb.FromHsl(h, s, l);
        Assert.InRange(Math.Abs(back.R - r), 0, 1);
        Assert.InRange(Math.Abs(back.G - g), 0, 1);
        Assert.InRange(Math.Abs(back.B - b), 0, 1);
    }

    [Fact]
    public void MatchAccent_IsOffByDefault_AndSurvivesTheConfigFile()
    {
        using var lib = new TempLibrary();
        Assert.False(EngineConfig.Load(lib.Root).MatchAccent);
        EngineConfig.Update(lib.Root, c => c.MatchAccent = true);
        Assert.True(EngineConfig.Load(lib.Root).MatchAccent);
    }
}
