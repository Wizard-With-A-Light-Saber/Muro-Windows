using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Muro.Core;

/// Muro in eleven languages, as the macOS app has since 6.1: English, Simplified and Traditional Chinese, Japanese,
/// Korean, Spanish, French, German, Italian, Brazilian Portuguese and Russian. The English sentence is the key, so a
/// string nobody has translated (a new one, a rare error) simply shows in English. Each language is one embedded JSON
/// file, <c>Strings/&lt;code&gt;.json</c>, of {"English text": "translation"}. The choice is in AppSettings.Language
/// (null follows the Windows display language).
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"), ("ko", "한국어"),
        ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"), ("it", "Italiano"), ("pt-BR", "Português (Brasil)"), ("ru", "Русский"),
    ];

    private static Dictionary<string, string> _table = [];

    /// The language in use now, one of the codes in <see cref="Languages"/>.
    public static string Language { get; private set; } = "en";

    /// Switches language. <paramref name="chosen"/> is a code from <see cref="Languages"/>, or null to follow Windows.
    /// Does nothing when the language is already in use, so it is cheap to call before every menu.
    public static void Use(string? chosen)
    {
        var code = Resolve(string.IsNullOrEmpty(chosen) ? CultureInfo.CurrentUICulture.Name : chosen);
        if (code == Language && (code == "en" || _table.Count > 0)) return;
        Language = code;
        _table = code == "en" ? [] : Load(code);
    }

    /// The translation of an English string, or the string itself.
    public static string T(string english) => _table.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    /// Maps a culture name ("de-AT", "zh-TW", "pt-PT", "ja-JP") to the nearest language Muro has; English when none.
    public static string Resolve(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en";
        var exact = Languages.FirstOrDefault(l => l.Code.Equals(culture, StringComparison.OrdinalIgnoreCase));
        if (exact.Code is not null) return exact.Code;
        var lower = culture.ToLowerInvariant();
        if (lower.StartsWith("zh"))
            return lower.Contains("hant") || lower.Contains("tw") || lower.Contains("hk") || lower.Contains("mo") ? "zh-Hant" : "zh-Hans";
        if (lower.StartsWith("pt")) return "pt-BR";
        var prefix = lower.Split('-')[0];
        return Languages.FirstOrDefault(l => l.Code == prefix).Code ?? "en";
    }

    private static Dictionary<string, string> Load(string code)
    {
        var assembly = typeof(Loc).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + code + ".json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return [];
        try
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (JsonException) { return []; }
    }
}
