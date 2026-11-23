using System.Reflection;
using System.Text.Json;
using Muro.Core;

namespace Muro.Core.Tests;

public class LocTests
{
    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("de-AT", "de")]
    [InlineData("ja-JP", "ja")]
    [InlineData("es-MX", "es")]
    [InlineData("en-GB", "en")]
    [InlineData("sv-SE", "en")]     // a language Muro does not have
    [InlineData("", "en")]
    public void Windows_CultureNames_MapToTheNearestLanguage(string culture, string expected) =>
        Assert.Equal(expected, Loc.Resolve(culture));

    [Fact]
    public void ElevenLanguages_EnglishFirst()
    {
        Assert.Equal(11, Loc.Languages.Length);
        Assert.Equal("en", Loc.Languages[0].Code);
    }

    [Fact]
    public void T_Translates_FallsBackToEnglish_AndSwitchesBack()
    {
        try
        {
            Loc.Use("ja");
            Assert.NotEqual("Cancel", Loc.T("Cancel"));
            Assert.Equal("a sentence nobody translated", Loc.T("a sentence nobody translated"));
            Loc.Use("en");
            Assert.Equal("Cancel", Loc.T("Cancel"));
        }
        finally { Loc.Use("en"); }
    }

    private static Dictionary<string, string> Read(string code)
    {
        var assembly = typeof(Loc).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + code + ".json"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    [Fact]
    public void EveryLanguage_HasEveryString_KeepsTheBrandAndLinks_AndEndsLikeTheEnglish()
    {
        var english = Read("es").Keys.ToList();   // the key set is the same for every language
        Assert.Equal(143, english.Count);
        foreach (var (code, _) in Loc.Languages.Skip(1))
        {
            var table = Read(code);
            Assert.Equal(english.Count, table.Count);
            foreach (var key in english)
            {
                Assert.True(table.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated), code + " is missing: " + key);
                if (key.Contains("Muro") && key != "Muro's Pick") Assert.True(translated!.Contains("Muro"), code + " lost the name Muro in: " + key);
                if (key.Contains("muro://")) Assert.Contains("muro://", translated);
                // A sentence stays a sentence: a final full stop is kept (Chinese and Japanese write their own).
                if (key.EndsWith('.') && key.Length > 30) Assert.True(translated!.TrimEnd().EndsWith('.') || translated.TrimEnd().EndsWith('。'), code + " dropped the full stop: " + key);
            }
        }
    }
}
