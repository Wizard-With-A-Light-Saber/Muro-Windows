using System.Text.Json;
using Muro.Core.Updates;

namespace Muro.Core.Tests;

public class UpdateTests
{
    // ---- AppVersion (ported from the macOS AppVersionTests)

    [Theory]
    [InlineData("1.10", "1.9", true)]
    [InlineData("1.9", "1.10", false)]
    [InlineData("10.0", "9.9", true)]
    [InlineData("2.0", "3.0", false)]
    [InlineData("2.0", "2.0", false)]
    [InlineData("2.0.0", "2.0", false)]
    [InlineData("2", "2.0.0", false)]
    [InlineData("2.0.1", "2.0", true)]
    [InlineData("v3.0", "2.0", true)]
    [InlineData("v2.0", "2.0", false)]
    [InlineData("", "2.0", false)]
    [InlineData("beta", "2.0", false)]
    public void Versions_AreComparedAsNumbers(string candidate, string current, bool newer) =>
        Assert.Equal(newer, AppVersion.IsNewer(candidate, current));

    [Fact]
    public void ThisBuildHasAVersion() => Assert.Matches(@"^\d+\.\d+", AppVersion.Current);

    // ---- Release notes (ported from the macOS ReleaseNotesTests)

    [Fact]
    public void Headings_BecomeSections()
    {
        var notes = ReleaseNotes.Parse("## New\n- Automations\n- Pause after\n\n## Fixed\n- Downloads are faster");
        Assert.Equal(2, notes.Count);
        Assert.Equal("New", notes[0].Name);
        Assert.Equal(["Automations", "Pause after"], notes[0].Lines);
        Assert.Equal("Fixed", notes[1].Name);
        Assert.Equal(["Downloads are faster"], notes[1].Lines);
    }

    [Fact]
    public void NotesWithoutHeadings_AndPlainProse_StillParse()
    {
        var bullets = Assert.Single(ReleaseNotes.Parse("- One\n- Two"));
        Assert.Null(bullets.Name);
        Assert.Equal(["One", "Two"], bullets.Lines);
        Assert.Equal(["This release fixes the lock screen."], ReleaseNotes.Parse("This release fixes the lock screen.")[0].Lines);
    }

    [Fact]
    public void Decoration_IsStripped_AndBulletStylesAreRecognised()
    {
        Assert.Equal(["Bold and code and a link"], ReleaseNotes.Parse("- **Bold** and `code` and [a link](https://example.com)")[0].Lines);
        Assert.Equal(["Star", "Plus", "Numbered"], ReleaseNotes.Parse("* Star\n+ Plus\n1. Numbered")[0].Lines);
        Assert.Equal(["1.5 million downloads"], ReleaseNotes.Parse("1.5 million downloads")[0].Lines);
    }

    [Fact]
    public void Chrome_IsSkipped()
    {
        var body = "- Real note\n---\n![screenshot](a.png)\n<img src=\"b.png\">\n**Full Changelog**: https://github.com/x/y/compare/v1...v2";
        Assert.Equal(["Real note"], ReleaseNotes.Parse(body)[0].Lines);
    }

    [Fact]
    public void EmptyBodies_GiveNoSections_AndLongOnesAreCapped()
    {
        Assert.Empty(ReleaseNotes.Parse(""));
        Assert.Empty(ReleaseNotes.Parse("\n\n   \n"));
        var total = ReleaseNotes.Parse(string.Join("\n", Enumerable.Range(1, 500).Select(i => "- Note " + i))).Sum(s => s.Lines.Count);
        Assert.Equal(ReleaseNotes.LineLimit, total);
    }

    // ---- ReleaseInfo

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string Release(string tag, string assets, string extra = "") =>
        $$"""{"tag_name":"{{tag}}","name":"Muro {{tag}}","html_url":"https://github.com/MrRockySL/Muro/releases/tag/{{tag}}","published_at":"2026-09-01T10:00:00Z","body":"## New\n- A thing","assets":[{{assets}}]{{extra}}}""";

    private static string Asset(string name) => $$"""{"name":"{{name}}","browser_download_url":"https://example.com/{{name}}"}""";

    [Fact]
    public void ReadsVersionNotesAndTheWindowsAsset()
    {
        var info = ReleaseInfo.From(Json(Release("v0.2.0", Asset("Muro-0.2.0.dmg") + "," + Asset("Muro-Setup-0.2.0.exe"))))!;
        Assert.Equal("0.2.0", info.Version);
        Assert.Null(info.Title);   // "Muro v0.2.0" only repeats the heading
        Assert.Equal("Muro-Setup-0.2.0.exe", info.DownloadUrl!.Segments[^1]);
        Assert.Equal("New", info.Notes[0].Name);
        Assert.NotNull(info.PublishedAt);
    }

    [Theory]
    [InlineData("Muro.dmg", false)]
    [InlineData("source.zip", false)]
    [InlineData("Muro-0.2-win-x64.zip", true)]
    [InlineData("Muro-Setup.exe", true)]
    [InlineData("Muro.msi", true)]
    [InlineData("demo.mp4", false)]
    public void OnlyWindowsAssetsCount(string name, bool windows) => Assert.Equal(windows, ReleaseInfo.IsWindowsAsset(name));

    [Fact]
    public void TheInstallerIsPreferredOverTheZip()
    {
        var info = ReleaseInfo.From(Json(Release("v1.0", Asset("Muro-win-x64.zip") + "," + Asset("Muro-Setup.exe"))))!;
        Assert.Equal("Muro-Setup.exe", info.DownloadUrl!.Segments[^1]);
    }

    [Fact]
    public void TheInstallerIsPreferredOverThePortableExe_AndBothOverTheZip()
    {
        var info = ReleaseInfo.From(Json(Release("v1.0", Asset("Muro-Portable-1.0-win-x64.exe") + "," + Asset("Muro-1.0-win-x64.zip") + "," + Asset("Muro-Setup-1.0.exe"))))!;
        Assert.Equal("Muro-Setup-1.0.exe", info.DownloadUrl!.Segments[^1]);
        var noSetup = ReleaseInfo.From(Json(Release("v1.0", Asset("Muro-1.0-win-x64.zip") + "," + Asset("Muro-Portable-1.0-win-x64.exe"))))!;
        Assert.Equal("Muro-Portable-1.0-win-x64.exe", noSetup.DownloadUrl!.Segments[^1]);
    }

    [Fact]
    public void MalformedPayloads_AreRefused()
    {
        Assert.Null(ReleaseInfo.From(Json("{}")));
        Assert.Null(ReleaseInfo.From(Json("""{"tag_name":"v4.0"}""")));
        Assert.Null(ReleaseInfo.From(Json("""{"tag_name":"v","html_url":"https://x.test/a"}""")));
        Assert.Null(ReleaseInfo.From(Json("""{"tag_name":"v4.0","html_url":7}""")));
        Assert.Null(ReleaseInfo.From(Json("[]")));
    }

    [Fact]
    public void ARealTitleIsKept_AndRepeatedOnesAreDropped()
    {
        var real = Release("v4.0", "").Replace("\"name\":\"Muro v4.0\"", "\"name\":\"Lock screen live wallpapers\"");
        Assert.Equal("Lock screen live wallpapers", ReleaseInfo.From(Json(real))!.Title);
        foreach (var name in new[] { "v4.0", "4.0", "Muro 4.0", "muro 4.0", "MURO V4.0", "  " })
            Assert.Null(ReleaseInfo.From(Json(Release("v4.0", "").Replace("Muro v4.0", name)))!.Title);
    }

    // ---- UpdateChecker: the macOS release must never read as a Windows update

    private static JsonElement Releases(params string[] releases) => Json("[" + string.Join(",", releases) + "]");

    [Fact]
    public void AMacOnlyLatestRelease_IsNotAnUpdate()
    {
        var releases = Releases(Release("v5.2", Asset("Muro-5.2.dmg")));
        Assert.Null(UpdateChecker.Pick(releases, "0.1.0"));
    }

    [Fact]
    public void AWindowsReleaseBehindAMacRelease_IsFound()
    {
        var releases = Releases(Release("v5.2", Asset("Muro-5.2.dmg")), Release("v0.3.0", Asset("Muro-Setup-0.3.0.exe")));
        Assert.Equal("0.3.0", UpdateChecker.Pick(releases, "0.1.0")!.Version);
    }

    [Fact]
    public void TheNewestWindowsRelease_Wins_AndCurrentOrOlderIsNoUpdate()
    {
        var releases = Releases(Release("v0.2.0", Asset("a.exe")), Release("v0.10.0", Asset("b.exe")), Release("v0.9.0", Asset("c.exe")));
        Assert.Equal("0.10.0", UpdateChecker.Pick(releases, "0.2.0")!.Version);
        Assert.Null(UpdateChecker.Pick(releases, "0.10.0"));
    }

    [Fact]
    public void DraftsAndPrereleases_AreSkipped()
    {
        var releases = Releases(Release("v0.5.0", Asset("a.exe"), ",\"prerelease\":true"), Release("v0.6.0", Asset("a.exe"), ",\"draft\":true"));
        Assert.Null(UpdateChecker.Pick(releases, "0.1.0"));
        Assert.Null(UpdateChecker.Pick(Json("{}"), "0.1.0"));
    }
}
