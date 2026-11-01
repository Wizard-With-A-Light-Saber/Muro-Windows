using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Muro.Core;
using Muro.Platform;
using Muro.Playback;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace Muro.Engine;

/// The optional effects: day/night tint, mouse parallax, audio flash.
///   - the tint and the flash are click-through windows over the wallpaper that the desktop compositor blends,
///     because mpv's brightness control does nothing in the low-cost playback mode;
///   - the mouse effect nudges mpv's own pan, which is plain geometry and works in that mode;
///   - one message-loop timer, as slow as the enabled effects allow (50 ms audio/mouse, 2 s stats, 60 s clock);
///   - values are only written when they changed, so a still mouse or silence costs nothing;
///   - audio is loopback-captured only while it is wanted, and reduced to one loudness number per buffer.
internal sealed class EffectsRunner : IDisposable
{
    public readonly record struct Target(MpvPlayer Player, Rect Bounds, bool Paused, DesktopHost.Target? Host);

    // Zoom in a little while the pointer effect is on, so panning never shows the edge of the picture.
    private const double ZoomWhenPanning = 0.04, PanAmplitude = 0.012;
    private const double NightAlpha = 56;    // 22% of a deep blue at full night
    private const double FlashAlpha = 16;    // 6% white at the loudest beat
    private static readonly Color Night = Color.FromArgb(8, 12, 36);

    private sealed class PlayerFx
    {
        public readonly Dictionary<string, string> Sent = new();
        public double PanX, PanY;
        public nint Parent;
        public Rect Bounds;
        public TintLayer? Dim, Flash;

        public void DisposeLayers()
        {
            Dim?.Dispose(); Flash?.Dispose();
            Dim = Flash = null;
            Sent.Remove("dim"); Sent.Remove("flash");
        }
    }

    private readonly SystemEvents _events;
    private readonly Func<EngineConfig> _config;
    private readonly Func<List<Target>> _targets;
    private readonly Dictionary<MpvPlayer, PlayerFx> _state = new();
    private nuint _timer;
    private int _interval;
    private string _key = "";
    private double _pulse;
    private long _audioRetryAt;
    private bool _layerErrorLogged;

    private WasapiLoopbackCapture? _capture;
    private volatile float _level, _peak = 0.02f;
    private long _audioAt;

    public EffectsRunner(SystemEvents events, Func<EngineConfig> config, Func<List<Target>> targets)
    {
        _events = events;
        _config = config;
        _targets = targets;
    }

    /// Brings the timer and the audio capture in line with the config and which screens are playing.
    /// Cheap and safe to call often.
    public void Sync()
    {
        var fx = _config().Effects;
        var targets = _targets();
        Prune(targets);
        var want = fx.Any && targets.Any(t => !t.Paused);
        // Every flash step is a repaint of the whole screen layer, so audio alone runs at about 12 a second.
        var interval = !want ? 0 : fx.Cursor ? 50 : fx.Audio ? 80 : 60000;
        // The clock effect must keep running while paused (a still frame should still go dark at night).
        if (interval == 0 && fx.DayNight) interval = 60000;
        var key = $"{fx.DayNight}{fx.Cursor}{fx.Audio}";

        var wantAudio = want && fx.Audio;
        if (wantAudio && _capture is null && Environment.TickCount64 >= _audioRetryAt) StartAudio();
        else if (!wantAudio && _capture is not null) StopAudio();

        var changed = interval != _interval || key != _key;
        if (interval != _interval)
        {
            _events.StopTimer(_timer);
            _timer = interval > 0 ? _events.StartTimer(interval, Tick, repeat: true) : 0;
            _interval = interval;
        }
        _key = key;
        // A screen that appeared since the last tick gets the effects at once, not after a slow interval.
        if (changed || (fx.Any && targets.Any(t => !_state.ContainsKey(t.Player)))) Tick();
        if (!fx.Any) _state.Clear();   // the tick above has just taken every layer down and the pan back to neutral
    }

    /// Players that are gone (a monitor unplugged, a wallpaper rebuilt) take their layers with them: the layers are
    /// siblings of the wallpaper window, so closing that window would not close them.
    private void Prune(List<Target> targets)
    {
        foreach (var gone in _state.Keys.Where(p => targets.All(t => t.Player != p)).ToList())
        {
            _state[gone].DisposeLayers();
            _state.Remove(gone);
        }
    }

    private void Tick()
    {
        var fx = _config().Effects;
        var targets = _targets();
        Prune(targets);
        var now = Environment.TickCount64;

        var dim = fx.DayNight ? (byte)Math.Round(EffectMath.Night(DateTime.Now.TimeOfDay) * NightAlpha) : (byte)0;
        if (fx.Audio)
        {
            var heard = now - Interlocked.Read(ref _audioAt) < 300 ? _level : 0f;
            _pulse = Math.Max(heard, _pulse * 0.75);   // quick to rise, slower to fall
        }
        else _pulse = 0;
        // Steps of 2 give nine levels instead of seventeen: fewer repaints, and the eye cannot tell.
        var flash = (byte)(Math.Round(_pulse * FlashAlpha / 2) * 2);

        var cursor = fx.Cursor ? SystemLoad.Cursor() : null;

        foreach (var t in targets)
        {
            if (!_state.TryGetValue(t.Player, out var s)) _state[t.Player] = s = new PlayerFx();
            if (t.Host is { } host)
            {
                if (s.Parent != host.Parent || s.Bounds != t.Bounds) { s.DisposeLayers(); s.Parent = host.Parent; s.Bounds = t.Bounds; }
                s.Dim = Layer(s.Dim, dim > 0, () => new TintLayer(host, t.Bounds, Night));
                if (s.Dim is not null && Changed(s, "dim", dim)) s.Dim.SetAlpha(dim);
                if (t.Paused && fx.Any) continue;   // the tint above stays; nothing else moves on a frozen screen
                s.Flash = Layer(s.Flash, fx.Audio, () => new TintLayer(host, t.Bounds, Color.White));
                if (s.Flash is not null && Changed(s, "flash", flash)) s.Flash.SetAlpha(flash);
            }
            else if (t.Paused && fx.Any) continue;

            // Pan and zoom are mpv geometry, so unlike brightness they work in the low-cost mode. Written only
            // while the effect is on, and once more to put the picture back when it goes off.
            if (!fx.Cursor && !s.Sent.ContainsKey("video-zoom")) continue;
            var (tx, ty) = cursor is { } c ? EffectMath.CursorOn(t.Bounds, c.X, c.Y) : (0, 0);
            s.PanX += (-tx * PanAmplitude - s.PanX) * 0.2;
            s.PanY += (-ty * PanAmplitude - s.PanY) * 0.2;
            var zoom = fx.Cursor ? ZoomWhenPanning : 0;
            SetProperty(t.Player, s, "video-zoom", zoom.ToString(CultureInfo.InvariantCulture));
            SetProperty(t.Player, s, "video-pan-x", s.PanX.ToString("0.0000", CultureInfo.InvariantCulture));
            SetProperty(t.Player, s, "video-pan-y", s.PanY.ToString("0.0000", CultureInfo.InvariantCulture));
            if (!fx.Cursor && s.PanX == 0 && s.PanY == 0) s.Sent.Remove("video-zoom");
        }
    }

    /// Creates the layer when it is wanted and takes it down when it is not. A window that cannot be created is
    /// logged once, not every tick.
    private T? Layer<T>(T? current, bool wanted, Func<T> make) where T : class, IDisposable
    {
        if (!wanted) { current?.Dispose(); return null; }
        if (current is not null) return current;
        try { return make(); }
        catch (Exception ex)
        {
            if (!_layerErrorLogged) Log.Info("effect layer failed: " + ex.Message);
            _layerErrorLogged = true;
            return null;
        }
    }

    private static bool Changed(PlayerFx s, string name, byte value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (s.Sent.TryGetValue(name, out var old) && old == text) return false;
        s.Sent[name] = text;
        return true;
    }

    /// Writes one mpv property if it differs from what this player already has. A refusal is remembered as sent, so
    /// it is logged once instead of every tick.
    private static void SetProperty(MpvPlayer player, PlayerFx s, string name, string value)
    {
        if (s.Sent.TryGetValue(name, out var old) && old == value) return;
        s.Sent[name] = value;
        try { player.SetProperty(name, value); }
        catch (Exception ex) { Log.Info($"effect {name} failed: {ex.Message}"); }
    }

    private void StartAudio()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            if (capture.WaveFormat.BitsPerSample != 32) { capture.Dispose(); throw new NotSupportedException("the output is not 32-bit float"); }
            capture.DataAvailable += OnAudio;
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            Log.Info("audio effect unavailable: " + ex.Message);
            _audioRetryAt = Environment.TickCount64 + 60_000;
        }
    }

    // Runs on NAudio's capture thread, about every 50 ms: one loudness number, scaled by a slowly falling peak.
    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded / 4 * 4));
        var rms = (float)EffectMath.Rms(samples);
        _peak = Math.Max(Math.Max(rms, 0.02f), _peak * 0.998f);
        _level = Math.Min(1f, rms / _peak);
        Interlocked.Exchange(ref _audioAt, Environment.TickCount64);
    }

    private void StopAudio()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        capture.DataAvailable -= OnAudio;
        try { capture.Dispose(); } catch (Exception ex) { Log.Info("audio effect stop: " + ex.Message); }
        _level = 0;
    }

    public void Dispose()
    {
        _events.StopTimer(_timer);
        StopAudio();
        foreach (var s in _state.Values) s.DisposeLayers();
        _state.Clear();
    }
}
