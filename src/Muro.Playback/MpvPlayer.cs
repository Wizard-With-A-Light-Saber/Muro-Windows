using System.Runtime.InteropServices;

namespace Muro.Playback;

public sealed class MpvException(string message) : Exception(message);

/// One libmpv instance rendering into an existing window. Hardware decode (d3d11va), looping,
/// no audio. Paused playback costs no CPU: mpv stops decoding and presenting.
public sealed class MpvPlayer : IDisposable
{
    private nint _ctx;

    public MpvPlayer(nint hostWindow, string? logFile = null, bool fill = true)
    {
        _ctx = MpvNative.mpv_create();
        if (_ctx == 0) throw new MpvException("mpv_create failed");
        Opt("wid", ((long)hostWindow).ToString());
        Opt("vo", "gpu");
        Opt("gpu-api", "d3d11");
        Opt("hwdec", "d3d11va");
        Opt("loop-file", "inf");
        Opt("aid", "no");
        Opt("keep-open", "yes");
        Opt("panscan", fill ? "1.0" : "0.0");   // wallpapers fill the monitor and crop; a preview shows the whole picture
        Opt("input-default-bindings", "no");
        Opt("input-vo-keyboard", "no");
        Opt("osc", "no");
        Opt("osd-level", "0");
        Opt("cursor-autohide", "no");
        Opt("idle", "yes");
        // Cheapest scalers: the wallpaper is rarely scaled by much and the GPU stays quiet.
        Opt("scale", "bilinear"); Opt("cscale", "bilinear"); Opt("dscale", "bilinear");
        // Dumb mode skips libplacebo's shader pipeline: measured ~22% vs 31-49% of one core at 4K60.
        // Fine for SDR wallpapers; revisit if HDR content ever matters.
        Opt("gpu-dumb-mode", "yes"); Opt("gpu-shader-cache", "no"); Opt("icc-cache", "no");
        Opt("dither-depth", "no"); Opt("deband", "no"); Opt("interpolation", "no");
        Opt("video-sync", "audio");
        if (logFile != null && Environment.GetEnvironmentVariable("MURO_MPV_LOG") == "1")
        {
            Opt("log-file", logFile); Opt("msg-level", "all=v");
        }
        // Benchmark hook: MURO_MPV_OPTS="video-sync=desync,vo=gpu-next" overrides the defaults above.
        foreach (var kv in (Environment.GetEnvironmentVariable("MURO_MPV_OPTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i > 0) Opt(kv[..i], kv[(i + 1)..]);
        }
        Check(MpvNative.mpv_initialize(_ctx), "mpv_initialize");
    }

    private void Opt(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(_ctx, name, value), $"option {name}");

    public void Load(string path) => Command("loadfile", path, "replace");

    public bool Paused
    {
        get => GetString("pause") == "yes";
        set => Check(MpvNative.mpv_set_property_string(_ctx, "pause", value ? "yes" : "no"), "pause");
    }

    /// Caps output at 30 fps for Efficient mode; 0 restores the source rate.
    public void SetFpsCap(int fps) =>
        Check(MpvNative.mpv_set_property_string(_ctx, "vf", fps > 0 ? $"fps=fps={fps}" : ""), "vf");

    public string? GetString(string name)
    {
        var p = MpvNative.mpv_get_property_string(_ctx, name);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); } finally { MpvNative.mpv_free(p); }
    }

    private void Command(params string[] args)
    {
        var mem = new nint[args.Length + 1];
        var block = Marshal.AllocHGlobal(mem.Length * nint.Size);
        try
        {
            for (int i = 0; i < args.Length; i++) mem[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            Marshal.Copy(mem, 0, block, mem.Length);
            Check(MpvNative.mpv_command(_ctx, block), args[0]);
        }
        finally
        {
            foreach (var m in mem) if (m != 0) Marshal.FreeCoTaskMem(m);
            Marshal.FreeHGlobal(block);
        }
    }

    private static void Check(int code, string what)
    {
        if (code >= 0) return;
        throw new MpvException($"{what}: {Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(code))}");
    }

    public void Dispose()
    {
        if (_ctx == 0) return;
        MpvNative.mpv_terminate_destroy(_ctx);
        _ctx = 0;
    }
}
