namespace Muro.Core.Updates;

/// Compares dotted version strings. Compared as text, "1.10" sorts before "1.9" and every install
/// would quietly stop hearing about updates, so the parts are compared as numbers.
public static class AppVersion
{
    /// This build's version, from the assembly (Directory.Build.props), without any "+commit" suffix.
    public static string Current { get; } = (typeof(AppVersion).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0")
        .Split('+')[0];

    /// True when <paramref name="candidate"/> is strictly newer. Missing parts count as zero, so "2"
    /// and "2.0.0" are the same version. A leading "v" is ignored (release tags are written "v3.0").
    public static bool IsNewer(string candidate, string current)
    {
        var left = Parts(candidate);
        var right = Parts(current);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var a = i < left.Length ? left[i] : 0;
            var b = i < right.Length ? right[i] : 0;
            if (a != b) return a > b;
        }
        return false;
    }

    private static int[] Parts(string version)
    {
        var trimmed = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        return trimmed.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
