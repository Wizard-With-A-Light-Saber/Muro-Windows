using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Muro.Core;

namespace Muro.Platform;

/// The audio visualiser: a click-through strip along the bottom of one screen, parented like the other effect layers
/// so it sits above the wallpaper and below the desktop icons. Everything of one colour (the key) is transparent, so
/// the wallpaper shows through wherever nothing is drawn. Only the bottom quarter of the screen is covered. Four
/// styles: spectrum bars, waveform, particles and VU meters.
///
/// Why a colour key and not per-pixel alpha (UpdateLayeredWindow): in a child of the desktop's WorkerW window the
/// compositor accepted every per-pixel-alpha frame, with no error, and never showed one. The fixed-alpha windows the
/// tint uses do show, and a colour key is a fixed-alpha window, so that is what this is. The price is no soft edges
/// and no see-through: shapes are solid colours.
public sealed class VisualizerLayer : IDisposable
{
    private const string ClassName = "MuroVizLayer";
    private const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_COLORKEY = 1, LWA_ALPHA = 2, WM_PAINT = 0x000F, SRCCOPY = 0x00CC0020;
    /// Nothing else is drawn in this colour. COLORREF is 0x00BBGGRR.
    private static readonly Color Key = Color.FromArgb(255, 1, 0, 1);
    private const uint KeyRef = 0x00010001;
    private static readonly Native.WndProc Proc = WndProc;
    private static readonly Dictionary<nint, VisualizerLayer> Live = [];
    private static bool _registered;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }

    [DllImport("gdi32")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32")] private static extern nint CreateDIBSection(nint hdc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint hdc);

    private sealed class Particle { public float X, Y, Vx, Vy, Life, Size, Hue; }

    private readonly int _w, _h;
    private nint _hwnd, _memDc, _dib, _oldBitmap;
    private Bitmap? _surface;
    private Graphics? _g;
    private readonly LinearGradientBrush _barBrush;
    private readonly float[] _bars = new float[VizFrame.BandCount], _peaks = new float[VizFrame.BandCount];
    private readonly List<Particle> _particles = [];
    private readonly Random _random = new();
    private float _vuL, _vuR, _holdL, _holdR;
    private int _holdLAge, _holdRAge;
    private bool _blank = true;

    /// Frames painted so far, for the log.
    public int Presented { get; private set; }

    public VisualizerLayer(DesktopHost.Target target, Rect screen)
    {
        _w = (int)screen.Width;
        _h = (int)(screen.Height * 0.26);
        _barBrush = new LinearGradientBrush(new Rectangle(0, 0, 1, _h), Color.FromArgb(170, 90, 255), Color.FromArgb(0, 220, 200), 90f);
        Register();
        var origin = new Native.POINT { X = (int)screen.X, Y = (int)screen.Bottom - _h };
        Native.ScreenToClient(target.Parent, ref origin);
        _hwnd = Native.CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT, ClassName, "Muro visualiser", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
            origin.X, origin.Y, _w, _h, target.Parent, 0, Native.GetModuleHandle(null), 0);
        if (_hwnd == 0) { Dispose(); throw new InvalidOperationException("visualiser window failed"); }
        Live[_hwnd] = this;
        Native.SetLayeredWindowAttributes(_hwnd, KeyRef, 255, LWA_COLORKEY | LWA_ALPHA);
        Native.SetWindowPos(_hwnd, 0, origin.X, origin.Y, _w, _h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        // One top-down 32-bit surface that GDI+ draws into and the window paints from, so no bitmap is made per frame.
        var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = _w, Height = -_h, Planes = 1, BitCount = 32 };
        _memDc = CreateCompatibleDC(0);
        _dib = CreateDIBSection(_memDc, ref header, 0, out var bits, 0, 0);
        if (_dib == 0 || bits == 0) { Dispose(); throw new InvalidOperationException("visualiser surface failed"); }
        _oldBitmap = SelectObject(_memDc, _dib);
        _surface = new Bitmap(_w, _h, _w * 4, PixelFormat.Format32bppRgb, bits);
        _g = Graphics.FromImage(_surface);
        _g.Clear(Key);
        _g.SmoothingMode = SmoothingMode.None;   // an edge blended with the key colour would leave a dark fringe
    }

    /// Draws one frame in the given style, or does nothing at all while there is nothing to show: a quiet PC costs no
    /// drawing and no compositor work.
    public void Draw(string style, VizFrame f)
    {
        if (_hwnd == 0 || _g is null) return;
        Smooth(f);
        var alive = !f.Silent || _bars.Max() > 0.02f || _vuL > 0.02f || _vuR > 0.02f || _particles.Count > 0;
        if (!alive)
        {
            if (_blank) return;
            _g.Clear(Key);
            Present();
            _blank = true;
            return;
        }

        _g.Clear(Key);
        switch (style)
        {
            case "waveform": DrawWave(f); break;
            case "particles": DrawParticles(f); break;
            case "meters": DrawMeters(); break;
            default: DrawBars(); break;
        }
        Present();
        _blank = false;
    }

    private void Smooth(VizFrame f)
    {
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = Math.Max(f.Bands[i], _bars[i] * 0.86f);   // quick up, slower down
            _peaks[i] = Math.Max(_bars[i], _peaks[i] - 0.012f);
        }
        _vuL = Math.Max(f.Left, _vuL * 0.9f);
        _vuR = Math.Max(f.Right, _vuR * 0.9f);
        Hold(ref _holdL, ref _holdLAge, _vuL);
        Hold(ref _holdR, ref _holdRAge, _vuR);
    }

    private static void Hold(ref float hold, ref int age, float level)
    {
        if (level >= hold) { hold = level; age = 0; }
        else if (++age > 20) hold = Math.Max(level, hold - 0.02f);
    }

    private void DrawBars()
    {
        var n = _bars.Length;
        var left = _w * 0.1f;
        var gap = _w * 0.004f;
        var barW = (_w * 0.8f - gap * (n - 1)) / n;
        var usable = _h * 0.92f;
        using var cap = new SolidBrush(Color.White);
        for (var i = 0; i < n; i++)
        {
            var x = left + i * (barW + gap);
            var height = Math.Max(2f, _bars[i] * usable);
            _g!.FillRectangle(_barBrush, x, _h - height, barW, height);
            _g.FillRectangle(cap, x, _h - Math.Max(2f, _peaks[i] * usable) - 4, barW, 3);
        }
    }

    private void DrawWave(VizFrame f)
    {
        var n = f.Wave.Length;
        var points = new PointF[n];
        var mid = _h / 2f;
        for (var i = 0; i < n; i++)
            points[i] = new PointF(_w * 0.05f + i * (_w * 0.9f / (n - 1)), mid - Math.Clamp(f.Wave[i] * 2.4f, -1f, 1f) * _h * 0.45f);
        // A wide teal line under a thin white one reads as a glow, without needing see-through colours.
        using (var glow = new Pen(Color.FromArgb(0, 150, 140), 8) { LineJoin = LineJoin.Round }) _g!.DrawLines(glow, points);
        using (var core = new Pen(Color.FromArgb(235, 250, 255), 3) { LineJoin = LineJoin.Round }) _g!.DrawLines(core, points);
    }

    private void DrawParticles(VizFrame f)
    {
        var spawn = (int)(f.Bass * 5) + (f.Beat ? 14 : 0);
        for (var i = 0; i < spawn && _particles.Count < 180; i++)
        {
            var x = (float)_random.NextDouble() * _w;
            _particles.Add(new Particle
            {
                X = x, Y = _h - 4, Vx = ((float)_random.NextDouble() - 0.5f) * 1.4f,
                Vy = -(1.5f + (float)_random.NextDouble() * 3.5f) * (0.7f + f.Bass),
                Life = 1f, Size = 3 + (float)_random.NextDouble() * 7, Hue = x / _w,
            });
        }
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy *= 0.985f; p.Life -= 0.017f;
            if (p.Life <= 0 || p.Y < 0) { _particles.RemoveAt(i); continue; }
            // Cannot fade to see-through, so a dying particle shrinks instead.
            var size = Math.Max(1.5f, p.Size * p.Life);
            using var brush = new SolidBrush(Ramp(p.Hue));
            _g!.FillEllipse(brush, p.X - size / 2, p.Y - size / 2, size, size);
        }
    }

    /// Teal on the left, through violet, to pink on the right.
    private static Color Ramp(float t)
    {
        var (from, to, k) = t < 0.5f ? (Color.FromArgb(0, 220, 200), Color.FromArgb(170, 90, 255), t * 2) : (Color.FromArgb(170, 90, 255), Color.FromArgb(255, 90, 170), (t - 0.5f) * 2);
        return Color.FromArgb((int)(from.R + (to.R - from.R) * k), (int)(from.G + (to.G - from.G) * k), (int)(from.B + (to.B - from.B) * k));
    }

    private void DrawMeters()
    {
        const int segments = 36;
        var width = _w * 0.5f;
        var left = (_w - width) / 2;
        var segW = width / segments;
        var segH = _h * 0.13f;
        Row(left, _h * 0.34f, _vuL, _holdL);
        Row(left, _h * 0.58f, _vuR, _holdR);

        void Row(float x0, float y, float level, float hold)
        {
            var lit = (int)Math.Round(level * segments);
            var held = Math.Clamp((int)Math.Round(hold * segments) - 1, 0, segments - 1);
            for (var i = 0; i < segments; i++)
            {
                var colour = i < segments * 0.7 ? Color.FromArgb(90, 220, 120) : i < segments * 0.9 ? Color.FromArgb(240, 210, 70) : Color.FromArgb(240, 80, 70);
                using var brush = new SolidBrush(i < lit || i == held ? colour : Color.FromArgb(58, 60, 68));   // an unlit segment is dark grey
                _g!.FillRectangle(brush, x0 + i * segW, y, segW * 0.78f, segH);
            }
        }
    }

    private void Present()
    {
        Native.InvalidateRect(_hwnd, 0, false);
        Native.UpdateWindow(_hwnd);
        Presented++;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == Native.WM_ERASEBKGND) return 1;
        if (msg == WM_PAINT && Live.TryGetValue(hwnd, out var layer))
        {
            var hdc = Native.BeginPaint(hwnd, out var paint);
            try { layer._g?.Flush(FlushIntention.Sync); BitBlt(hdc, 0, 0, layer._w, layer._h, layer._memDc, 0, 0, SRCCOPY); }
            finally { Native.EndPaint(hwnd, ref paint); }
            return 0;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Proc,
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        _registered = true;
    }

    public void Dispose()
    {
        if (_hwnd != 0) { Live.Remove(_hwnd); Native.DestroyWindow(_hwnd); _hwnd = 0; }
        _g?.Dispose(); _g = null;
        _surface?.Dispose(); _surface = null;
        _barBrush.Dispose();
        if (_memDc != 0 && _oldBitmap != 0) SelectObject(_memDc, _oldBitmap);
        if (_dib != 0) DeleteObject(_dib);
        if (_memDc != 0) DeleteDC(_memDc);
        _dib = _memDc = _oldBitmap = 0;
    }
}
