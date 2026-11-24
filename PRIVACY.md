# Privacy policy

Last updated: 2026-11-24 (Muro for Windows 1.0.0)

**In short:** Muro has no account, no ads, no analytics and no crash reporting. It does not send your files, your
wallpapers or what you listen to anywhere. It connects to the internet only to list and download wallpapers and to check
for updates.

## What stays on your PC

Muro keeps its data in `%LOCALAPPDATA%\Muro` (or the folder you choose for downloads):

- your library (`library.json`) and the wallpapers you downloaded or imported, with their pictures;
- your settings (`config.json`, `app-settings.json`), playlists and automations;
- logs (`logs\`), which record what Muro did (for example "now playing a wallpaper" or "a link was ignored"). They stay
  on your PC and are never sent anywhere;
- a profile folder for the web view that draws interactive wallpapers (`WebView2\`).

Videos and pages you import are copied into your library and are never uploaded.

## What Muro sends over the internet

| When | Where | What is sent |
|---|---|---|
| Opening Explore or Featured, and when the window comes to the front | `cdn.murowallpaper.com` (the Muro catalog, run by the author of the original Muro) | A request for the wallpaper list (`catalog.json`, `interactive.json`) |
| You press Download or Preview | the same host | A request for that wallpaper's files |
| Every few hours, and when you press Check for updates | `api.github.com` (this project's releases on GitHub) | A request for the release list, with the user agent `Muro-Windows` |
| You press Download on an update | `github.com`, in your browser | Whatever your browser sends |

Like any web request, these reach the servers with your IP address, which those operators may log under their own
policies (<https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>). Muro adds no account,
identifier or device information to them.

Interactive wallpapers are small web pages. Muro runs each in a locked-down view that can only reach its own folder on
your PC: it cannot go online, open windows or ask for permissions.

## Sound

The audio effects measure how loud the sound your PC plays is, using Windows' loopback capture of the sound output. Muro
reads only the loudness and frequency bands, in memory, to move the effect. It never records, saves or sends sound, and
it does not use your microphone.

## What Muro changes on your PC

Only what you turn on, and each change is undone when you turn it off or uninstall Muro:

- the desktop wallpaper (a still frame of the Muro wallpaper, so Windows prompts look right) and, if you choose, the
  lock screen picture and the screen saver;
- the Windows accent colour, if you turn on "Match the accent colour to the wallpaper";
- a Start with Windows entry, if you turn it on, and the `muro://` link handler (per user, no administrator rights).

## Third parties

The web view that draws interactive wallpapers is Microsoft Edge WebView2. Microsoft may collect diagnostic data from it
according to your Windows privacy settings and Microsoft's policy.

## Children

Muro is a wallpaper app and collects no personal data from anyone, including children.

## Your control

Switch features off in Settings, or uninstall Muro. To remove everything it stored, delete `%LOCALAPPDATA%\Muro`.

## Changes and contact

If this policy changes, the new version is published in this repository with a new date. Questions: open an issue at
<https://github.com/Wizard-With-A-Light-Saber/Muro-Windows/issues>.
