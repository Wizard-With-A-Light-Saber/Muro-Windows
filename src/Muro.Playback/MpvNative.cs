using System.Runtime.InteropServices;

namespace Muro.Playback;

internal static class MpvNative
{
    private const string Lib = "libmpv-2";

    [DllImport(Lib)] public static extern nint mpv_create();
    [DllImport(Lib)] public static extern int mpv_initialize(nint ctx);
    [DllImport(Lib)] public static extern void mpv_terminate_destroy(nint ctx);
    [DllImport(Lib)] public static extern int mpv_set_option_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] public static extern int mpv_set_property_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] public static extern int mpv_command(nint ctx, nint args);
    [DllImport(Lib)] public static extern nint mpv_get_property_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern void mpv_free(nint data);
    [DllImport(Lib)] public static extern nint mpv_error_string(int error);
}
