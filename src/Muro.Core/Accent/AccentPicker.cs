namespace Muro.Core.Accent;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// Hue in degrees (0-360), saturation and lightness (0-1).
    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360 / 360;
        double r = l, g = l, b = l;
        if (s > 0)
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Channel(p, q, h + 1.0 / 3);
            g = Channel(p, q, h);
            b = Channel(p, q, h - 1.0 / 3);
        }
        return new Rgb(To8(r), To8(g), To8(b));
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte To8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

/// Picks one accent colour for a picture, and the eight shades Windows keeps for it.
public static class AccentPicker
{
    private const int HueBuckets = 12;

    /// The dominant colour of a small RGB24 picture (3 bytes per pixel). Colourful mid-tones count most; near
    /// grey, near black and near white pixels barely count, so a dark picture with one glowing orange light
    /// gives orange, not black. The result is made vivid and mid-light so white text reads on it. A picture with
    /// no colour at all gives a grey of its average brightness.
    public static Rgb Pick(ReadOnlySpan<byte> rgb24)
    {
        var weight = new double[HueBuckets];
        var sin = new double[HueBuckets];
        var cos = new double[HueBuckets];
        var sat = new double[HueBuckets];
        var light = new double[HueBuckets];
        double sumR = 0, sumG = 0, sumB = 0;
        var pixels = rgb24.Length / 3;
        if (pixels == 0) return new Rgb(0x00, 0x78, 0xD4);   // Windows' default blue

        for (var i = 0; i + 2 < rgb24.Length; i += 3)
        {
            var color = new Rgb(rgb24[i], rgb24[i + 1], rgb24[i + 2]);
            sumR += color.R; sumG += color.G; sumB += color.B;
            var (h, s, l) = color.ToHsl();
            // Mid-tones weigh most; the ends of the lightness range and grey weigh nothing.
            var w = s * s * (1 - Math.Abs(2 * l - 1)) * (l is < 0.1 or > 0.92 ? 0 : 1);
            if (w <= 0) continue;
            var bucket = (int)(h / 360 * HueBuckets) % HueBuckets;
            weight[bucket] += w;
            sin[bucket] += w * Math.Sin(h * Math.PI / 180);
            cos[bucket] += w * Math.Cos(h * Math.PI / 180);
            sat[bucket] += w * s;
            light[bucket] += w * l;
        }

        var best = 0;
        for (var b = 1; b < HueBuckets; b++)
            if (weight[b] > weight[best]) best = b;

        // Almost no colour anywhere: a grey picture gets a grey accent.
        if (weight[best] < pixels * 0.002)
        {
            var grey = (byte)Math.Clamp(Math.Round((sumR + sumG + sumB) / (3.0 * pixels)), 70, 150);
            return new Rgb(grey, grey, grey);
        }

        var hue = Math.Atan2(sin[best], cos[best]) * 180 / Math.PI;
        var saturation = Math.Clamp(sat[best] / weight[best], 0.55, 0.95);
        var lightness = Math.Clamp(light[best] / weight[best], 0.38, 0.55);
        return Rgb.FromHsl(hue, saturation, lightness);
    }

    /// Windows' AccentPalette: eight shades. Three tints toward white, the accent itself, then four darker ones.
    public static Rgb[] Palette(Rgb accent)
    {
        var (h, s, l) = accent.ToHsl();
        Rgb Tint(double toward) => Rgb.FromHsl(h, s, l + (1 - l) * toward);
        Rgb Shade(double factor) => Rgb.FromHsl(h, s, l * factor);
        return [Tint(0.65), Tint(0.45), Tint(0.22), accent, Shade(0.78), Shade(0.58), Shade(0.38), Shade(0.25)];
    }
}
