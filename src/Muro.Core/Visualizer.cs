namespace Muro.Core;

/// The audio visualiser styles. The setting holds the style name, or null for off.
public static class VisualizerStyles
{
    public static readonly IReadOnlyList<(string Name, string Label)> All =
    [
        ("spectrum", "Spectrum bars"),
        ("waveform", "Waveform"),
        ("particles", "Particles"),
        ("meters", "VU meters"),
    ];

    /// A known style name, or "spectrum" for a name this version does not know (a config from a newer Muro).
    public static string Normalize(string name) => All.Any(s => s.Name == name) ? name : "spectrum";
}

/// One snapshot of the sound for the visualiser to draw. All values are 0..1 except the waveform (-1..1).
public sealed class VizFrame
{
    public const int BandCount = 48;
    public const int WaveCount = 256;

    public float[] Bands { get; } = new float[BandCount];
    public float[] Wave { get; } = new float[WaveCount];
    public float Left, Right, Bass;
    public bool Beat;
    /// Nothing has been heard for a moment.
    public bool Silent;
}

/// Turns a block of audio into frequency bands with a plain FFT. Pure maths, so it is tested without a sound card.
public static class Spectrum
{
    public const int FftSize = 1024;
    private const double LowHz = 40, HighHz = 16000;
    private const double FloorDb = -70, CeilingDb = -10;

    /// Fills <paramref name="bands"/> (0..1) from the last <see cref="FftSize"/> mono samples. Bands are spaced evenly
    /// on a log scale from 40 Hz to 16 kHz, like the ear hears them.
    public static void Bands(ReadOnlySpan<float> mono, int sampleRate, float[] bands)
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            var s = i < mono.Length ? mono[i] : 0f;
            re[i] = s * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann window
        }
        Fft(re, im);

        var binHz = sampleRate / (double)FftSize;
        var top = Math.Min(HighHz, sampleRate / 2.0);
        for (var b = 0; b < bands.Length; b++)
        {
            var lo = LowHz * Math.Pow(top / LowHz, b / (double)bands.Length);
            var hi = LowHz * Math.Pow(top / LowHz, (b + 1) / (double)bands.Length);
            var first = Math.Clamp((int)Math.Floor(lo / binHz), 1, FftSize / 2 - 1);
            var last = Math.Clamp((int)Math.Ceiling(hi / binHz), first + 1, FftSize / 2);
            double peak = 0;
            for (var k = first; k < last; k++) peak = Math.Max(peak, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
            // A full-scale sine gives an amplitude of FftSize / 4 with this window.
            bands[b] = ToUnit(peak / (FftSize / 4.0));
        }
    }

    /// Linear amplitude (1 = full scale) to 0..1 on a decibel scale, so quiet music still moves the bars.
    public static float ToUnit(double amplitude) =>
        (float)Math.Clamp((20 * Math.Log10(amplitude + 1e-9) - FloorDb) / (CeilingDb - FloorDb), 0, 1);

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wr = Math.Cos(angle);
            var wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var a = i + k;
                    var b = a + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
