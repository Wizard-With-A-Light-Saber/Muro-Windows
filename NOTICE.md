# Notice

## The app

Muro for Windows is made by [Wizard-With-A-Light-Saber](https://github.com/Wizard-With-A-Light-Saber). It is inspired by
the original [Muro for macOS](https://github.com/MrRockySL/Muro) by [MrRockySL](https://github.com/MrRockySL) and built
with the permission of its author. The source code in this repository is released under the
[PolyForm Shield License 1.0.0](LICENSE): you may use it, read it and send changes, but you may not release it, or a
changed version of it, as a competing product. The same licence terms the original uses. See [COPYRIGHT.md](COPYRIGHT.md)
and [AUTHORS.md](AUTHORS.md).

## The wallpapers

The wallpaper videos and interactive wallpapers shown in Explore come from the Muro catalog service run by the author of
the original Muro. They are **not** the work of the author of this Windows version and are **not** covered by this
repository's licence.

They are free videos and pages collected from the internet or contributed by their makers. Each one belongs to its
original creator and is offered under its own terms. Muro claims no ownership of any of them and grants no rights to them.
The four interactive wallpapers at the time of writing are Eclipse, White Sand and Grey Silk (MrRockySL) and Levi Ackerman
(bugbenderx).

If you are the creator of a wallpaper in the catalog and want it credited differently or removed, open an issue on
[the original Muro repository](https://github.com/MrRockySL/Muro/issues), which runs the catalog.

Videos and pages you import yourself stay on your PC. Muro converts them into your own library and never uploads them.

## Third-party software

Muro includes these components. Each keeps its own licence.

| Component | Used for | Licence |
|---|---|---|
| [mpv](https://mpv.io) (`libmpv-2.dll`, LGPL build) | Plays the wallpapers. Dynamically linked: replace the DLL to change it. | LGPL v2.1 or later |
| [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe`, LGPL build) | Imports videos, makes 30 fps copies and lock screen stills. Run as a separate program. | LGPL v2.1 or later |
| [NAudio](https://github.com/naudio/NAudio) | Listens to the loudness of the sound your PC plays, for the audio effects. | MIT |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | Draws interactive wallpapers. | Microsoft Software License Terms |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and .NET 8 | The app and its runtime, bundled so nothing needs installing. | MIT and Microsoft licence terms |
| System.Drawing.Common | Draws the overlays and placeholder pictures. | MIT |

The mpv and FFmpeg builds come from <https://github.com/zhongfly/mpv-winbuild> (release 2026-10-25-baa3e11d42); source
code and build scripts are there and at <https://github.com/mpv-player/mpv> and <https://ffmpeg.org>. The installer is built
with [Inno Setup](https://jrsoftware.org/isinfo.php), which is not part of the installed app. See also
[installer/THIRD-PARTY.txt](installer/THIRD-PARTY.txt).

## Trademarks

Windows, Microsoft Edge and WebView2 are trademarks of Microsoft. macOS is a trademark of Apple. Muro for Windows is not
affiliated with or endorsed by either company.
