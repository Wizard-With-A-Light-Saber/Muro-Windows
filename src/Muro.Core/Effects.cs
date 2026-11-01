namespace Muro.Core;

/// Optional extras drawn over the playing video. All off by default: each one costs a little CPU, GPU and RAM.
public sealed class EffectsConfig
{
    public bool DayNight { get; set; }
    public bool Cursor { get; set; }
    public bool Audio { get; set; }
    public bool Shader { get; set; }
    public string? ShaderPath { get; set; }

    public bool Any => DayNight || Cursor || Audio;
}

/// The pure maths behind the effects, kept out of the engine so it can be tested.
public static class EffectMath
{
    /// How dark the evening is, 0 (day) to 1 (night): ramps up 18:00 to 21:00, down 05:00 to 08:00.
    public static double Night(TimeSpan timeOfDay)
    {
        var h = timeOfDay.TotalHours;
        if (h >= 21 || h < 5) return 1;
        if (h >= 18) return (h - 18) / 3;
        if (h < 8) return (8 - h) / 3;
        return 0;
    }

    /// mpv brightness and saturation (-100..100) for the time of day: a little dimmer and calmer at night.
    public static (int Brightness, int Saturation) DayNight(TimeSpan timeOfDay)
    {
        var n = Night(timeOfDay);
        return ((int)Math.Round(-12 * n), (int)Math.Round(-10 * n));
    }

    /// Loudness of a block of float samples, 0..1.
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    /// Cursor position on a screen as -1..1 on each axis; 0,0 when the pointer is on another screen.
    public static (double X, double Y) CursorOn(Rect screen, double x, double y)
    {
        if (x < screen.X || x >= screen.Right || y < screen.Y || y >= screen.Bottom) return (0, 0);
        return ((x - screen.X) / screen.Width * 2 - 1, (y - screen.Y) / screen.Height * 2 - 1);
    }
}

/// What each effect adds while it runs, shown before the user turns it on. One measured run on one PC: treat as a
/// rough guide.
public static class EffectCost
{
    public sealed record Cost(string Name, string What, double Cpu, double Gpu, int RamMb);

    // Measured 2026-11-01 on the dev PC (8 cores, one 1080p display, Efficient wallpaper) with the overlay-window
    // implementation. CPU is the engine's share of the whole PC; GPU is the whole machine's GPU use, so the desktop
    // compositor's work on the overlays is included; RAM is the engine's working-set growth. Baseline: 1.0% CPU, 8.4%
    // GPU, 220 MB. Noise between two identical runs was about 0.2%, so figures at or below that are rounded up.
    public static readonly Cost DayNight = new("Day / night sync", "Tints the wallpaper a dim blue in the evening and clears it in the morning.", 0.1, 0.2, 8);
    public static readonly Cost Cursor = new("Mouse interaction", "The wallpaper shifts slightly as the pointer moves (a parallax effect).", 0.2, 0.2, 5);
    public static readonly Cost Audio = new("Audio reactivity", "The wallpaper flashes lightly with the sound your PC is playing.", 0.2, 1.2, 3);   // 2.4 at 20 updates a second, 1.2 at 12
    public static readonly Cost Shader = new("Custom shader", "Runs your own GLSL shader over the video. Expect a LARGE GPU increase: even a trivial shader needs mpv's full shader pipeline, and a complex one costs more. It cannot stay inside a 4% GPU budget.", 0.2, 15.0, 60);

    public static string Describe(Cost c) =>
        $"Adds about {c.Cpu:0.#}% CPU, {c.Gpu:0.#}% GPU and {c.RamMb} MB of memory.";
}
