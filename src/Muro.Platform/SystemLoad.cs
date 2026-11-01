using System.Runtime.InteropServices;

namespace Muro.Platform;

/// Cheap system readings for the effects: one Win32 call each, no hooks, no performance counters.
public static class SystemLoad
{
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }   // a ValueTuple has auto layout and cannot be marshalled
    [DllImport("user32")] private static extern bool GetCursorPos(out Pt point);

    public static (int X, int Y)? Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : null;
}
