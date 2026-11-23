using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Muro.Core;
using Muro.Platform;
using Muro.Playback;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Muro.Engine;

/// The optional effects: day/night tint, mouse parallax, audio flash. Each screen has its own set (its wallpaper's
/// own, or the Settings page's).
///   - the tint and the flash are click-through windows over the wallpaper that the desktop compositor blends,
///     because mpv's brightness control does nothing in the low-cost playback mode;
///   - the mouse effect nudges mpv's own pan, which is plain geometry and works in that mode;
///   - one message-loop timer, as slow as the enabled effects allow (50 ms mouse, 80 ms audio, 60 s clock);
///   - values are only written when they changed, so a still mouse or silence costs nothing;
///   - audio is loopback-captured only while some playing screen wants it, reduced to one loudness number per
///     buffer, and follows the default output device when that changes (headphones plugged in).
internal sealed class EffectsRunner : IDisposable
{
    public readonly record struct Target(MpvPlayer Player, Rect Bounds, bool Paused, DesktopHost.Target? Host, EffectsConfig Fx);

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
        public VisualizerLayer? Viz;

        public void DisposeLayers()
        {
            Dim?.Dispose(); Flash?.Dispose(); Viz?.Dispose();
            Dim = Flash = null; Viz = null;
            Sent.Remove("dim"); Sent.Remove("flash");
        }
    }

    /// Tells the runner when the default output device changes, on COM's thread.
    private sealed class DeviceWatcher(Action defaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) defaultChanged();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
    private bool _layerErrorLogged, _vizLogged, _audioErrorLogged;

    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;
    private DeviceWatcher? _watcher;
    private AudioEndpointVolume? _outVolume;
    private volatile float _level, _peak = 0.02f;
    private long _audioAt;

    // What the visualiser reads: the last few thousand mono samples (written by the capture thread, copied out on the
    // timer) and each channel's loudness. A torn read at worst shows one odd frame.
    private const int RingSize = 4096;
    private readonly float[] _ring = new float[RingSize];
    private int _ringWrite, _sampleRate = 48000, _channels = 2;
    private volatile float _vuLeft, _vuRight;
    private readonly float[] _mono = new float[1536];
    private readonly VizFrame _frame = new();
    private float _bassAverage;
    private int _beatCooldown;

    public EffectsRunner(SystemEvents events, Func<EngineConfig> config, Func<List<Target>> targets)
    {
        _events = events;
        _config = config;
        _targets = targets;
    }

    /// Brings the timer and the audio capture in line with each screen's effects and which screens are playing.
    /// Cheap and safe to call often.
    public void Sync()
    {
        var targets = _targets();
        Prune(targets);
        var live = targets.Where(t => !t.Paused && t.Fx.Any).ToList();
        var wantViz = live.Any(t => t.Fx.Visualizer is not null);
        var wantAudio = wantViz || live.Any(t => t.Fx.Audio);
        // The clock effect keeps running while paused: a still frame should still go dark at night.
        // Every flash step repaints a whole screen layer, so audio alone runs at about 12 a second; the visualiser
        // needs about 30 to look smooth.
        var interval = wantViz ? 33 : live.Any(t => t.Fx.Cursor) ? 50 : wantAudio ? 80 : live.Count > 0 || targets.Any(t => t.Fx.DayNight) ? 60000 : 0;
        var key = string.Join('|', targets.Select(t => $"{t.Fx.DayNight}{t.Fx.Cursor}{t.Fx.Audio}{t.Fx.Visualizer}{t.Paused}"));

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
        if (changed || targets.Any(t => t.Fx.Any && !_state.ContainsKey(t.Player))) Tick();
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
        var targets = _targets();
        Prune(targets);
        var now = Environment.TickCount64;

        var dim = (byte)Math.Round(EffectMath.Night(DateTime.Now.TimeOfDay) * NightAlpha);
        if (targets.Any(t => t.Fx.Audio))
        {
            var heard = now - Interlocked.Read(ref _audioAt) < 300 && !OutputSilenced() ? _level : 0f;
            _pulse = Math.Max(heard, _pulse * 0.75);   // quick to rise, slower to fall
        }
        else _pulse = 0;
        // Steps of 2 give nine levels instead of seventeen: fewer repaints, and the eye cannot tell.
        var flash = (byte)(Math.Round(_pulse * FlashAlpha / 2) * 2);
        var cursor = targets.Any(t => t.Fx.Cursor) ? SystemLoad.Cursor() : null;
        if (targets.Any(t => !t.Paused && t.Fx.Visualizer is not null)) BuildFrame(now);

        foreach (var t in targets)
        {
            var fx = t.Fx;
            if (!_state.TryGetValue(t.Player, out var s)) _state[t.Player] = s = new PlayerFx();
            if (t.Host is { } host)
            {
                if (s.Parent != host.Parent || s.Bounds != t.Bounds) { s.DisposeLayers(); s.Parent = host.Parent; s.Bounds = t.Bounds; }
                var screenDim = fx.DayNight ? dim : (byte)0;
                s.Dim = Layer(s.Dim, screenDim > 0, () => new TintLayer(host, t.Bounds, Night));
                if (s.Dim is not null && Changed(s, "dim", screenDim)) s.Dim.SetAlpha(screenDim);
                // A frozen screen shows no visualiser: it would be bars with no music behind a still picture.
                if (t.Paused && s.Viz is not null) { s.Viz.Dispose(); s.Viz = null; }
                if (t.Paused && fx.Any) continue;   // the tint above stays; nothing else moves on a frozen screen
                s.Flash = Layer(s.Flash, fx.Audio, () => new TintLayer(host, t.Bounds, Color.White));
                if (s.Flash is not null && Changed(s, "flash", flash)) s.Flash.SetAlpha(flash);
                s.Viz = Layer(s.Viz, fx.Visualizer is not null, () => new VisualizerLayer(host, t.Bounds));
                s.Viz?.Draw(VisualizerStyles.Normalize(fx.Visualizer!), _frame);
                if (s.Viz is { Presented: > 0 } && !_vizLogged)
                {
                    _vizLogged = true;
                    Log.Info("visualiser: first frame painted");
                }
            }
            else if (t.Paused && fx.Any) continue;

            // Pan and zoom are mpv geometry, so unlike brightness they work in the low-cost mode. Written only
            // while the effect is on, and once more to put the picture back when it goes off.
            if (!fx.Cursor && !s.Sent.ContainsKey("video-zoom")) continue;
            var (tx, ty) = cursor is { } c && fx.Cursor ? EffectMath.CursorOn(t.Bounds, c.X, c.Y) : (0, 0);
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
            _sampleRate = capture.WaveFormat.SampleRate;
            _channels = Math.Max(1, capture.WaveFormat.Channels);
            capture.DataAvailable += OnAudio;
            // The device went away or stopped: start again on whatever is the default now.
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null && ReferenceEquals(_capture, capture)) RestartAudioSoon(); };
            capture.StartRecording();
            _capture = capture;
            _enumerator = new MMDeviceEnumerator();
            try { _outVolume = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume; }
            catch { _outVolume = null; }   // no volume to read: behave as before and trust the capture
            _watcher = new DeviceWatcher(RestartAudioSoon);
            _enumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch (Exception ex)
        {
            if (!_audioErrorLogged) Log.Info("audio effect unavailable: " + ex.Message);
            _audioErrorLogged = true;
            // Right after a player closes the device can be busy for a moment. Try again soon, not in a minute:
            // a minute of no sound reads as a frozen visualiser.
            _audioRetryAt = Environment.TickCount64 + 3_000;
        }
    }

    /// Loopback capture reads the mix before the device's master volume and mute, so a muted or turned-down PC still
    /// delivers full-level audio. The visualiser and flash must follow what can be heard, not what is mixed.
    private bool OutputSilenced()
    {
        try { return _outVolume is { } v && (v.Mute || v.MasterVolumeLevelScalar < 0.01f); }
        catch { return false; }
    }

    /// Called from audio threads. Waits half a second, since Windows reports a device change a moment before the
    /// new device is ready, then drops the capture; <see cref="Sync"/> starts a fresh one on the new default.
    private void RestartAudioSoon() => _events.Post(() => _events.StartTimer(500, () => { StopAudio(); Sync(); }));

    // Runs on NAudio's capture thread, about every 80 ms: one loudness number, scaled by a slowly falling peak.
    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded / 4 * 4));
        var rms = (float)EffectMath.Rms(samples);
        _peak = Math.Max(Math.Max(rms, 0.02f), _peak * 0.998f);
        _level = Math.Min(1f, rms / _peak);

        // For the visualiser: keep the newest mono samples and each channel's loudness.
        var ch = _channels;
        var frames = samples.Length / ch;
        double left = 0, right = 0;
        var write = _ringWrite;
        for (var i = 0; i < frames; i++)
        {
            var l = samples[i * ch];
            var r = ch > 1 ? samples[i * ch + 1] : l;
            left += l * l; right += r * r;
            _ring[write++ & (RingSize - 1)] = (l + r) * 0.5f;
        }
        _ringWrite = write;
        if (frames > 0)
        {
            _vuLeft = Spectrum.ToUnit(Math.Sqrt(left / frames));
            _vuRight = Spectrum.ToUnit(Math.Sqrt(right / frames));
        }
        Interlocked.Exchange(ref _audioAt, Environment.TickCount64);
    }

    /// Fills <see cref="_frame"/> from the newest audio: frequency bands, a steady waveform window, channel levels and a
    /// beat flag. Runs on the timer, only while a screen is drawing a visualiser.
    private void BuildFrame(long now)
    {
        var f = _frame;
        // Silent when no buffer has arrived for a moment, or the buffers are all zeros: a player that is paused, or
        // closing, can keep a stream open and send silence for a long time, which used to leave flat bars on screen.
        f.Silent = now - Interlocked.Read(ref _audioAt) > 300 || Math.Max(_vuLeft, _vuRight) < 0.01f || OutputSilenced();
        if (f.Silent)
        {
            Array.Clear(f.Bands); Array.Clear(f.Wave);
            f.Left = f.Right = f.Bass = 0; f.Beat = false;
            return;
        }
        var end = _ringWrite;
        for (var i = 0; i < _mono.Length; i++) _mono[i] = _ring[(end - _mono.Length + i) & (RingSize - 1)];
        Spectrum.Bands(_mono.AsSpan(_mono.Length - Spectrum.FftSize), _sampleRate, f.Bands);

        // Start the waveform at a rising zero crossing so the line holds still instead of scrolling.
        var start = 0;
        for (var i = 0; i < 512; i++) if (_mono[i] < 0 && _mono[i + 1] >= 0) { start = i; break; }
        for (var i = 0; i < VizFrame.WaveCount; i++) f.Wave[i] = _mono[start + i * 4];

        f.Left = _vuLeft; f.Right = _vuRight;
        var bass = 0f;
        for (var i = 0; i < 6; i++) bass += f.Bands[i];
        f.Bass = bass /= 6;
        _bassAverage = _bassAverage * 0.95f + bass * 0.05f;
        _beatCooldown = Math.Max(0, _beatCooldown - 1);
        f.Beat = _beatCooldown == 0 && bass > _bassAverage * 1.3f + 0.08f;
        if (f.Beat) _beatCooldown = 6;
    }

    private void StopAudio()
    {
        var capture = _capture;
        _capture = null;
        if (_enumerator is not null)
        {
            try { if (_watcher is not null) _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
            _enumerator.Dispose();
        }
        _enumerator = null; _watcher = null;
        try { _outVolume?.Dispose(); } catch { }
        _outVolume = null;
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
