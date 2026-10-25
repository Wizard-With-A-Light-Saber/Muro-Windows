# Muro for Windows

Native Windows 11 port of Muro (live video wallpapers). Work in progress, built milestone by milestone.

Built with the permission of the Muro author (MrRockySL). Muro itself is under the PolyForm Shield 1.0.0
license; keep that permission on record before any public release. Wallpaper videos belong to their
original creators (see `../NOTICE.md`).

## Build

```powershell
./scripts/fetch-native.ps1        # libmpv + ffmpeg (LGPL builds) into third_party/
dotnet build Muro.sln -c Release
dotnet test tests/Muro.Core.Tests -c Release
```

## Engine harness (until the UI exists)

```powershell
Muro.Engine.exe <video> [--monitor N=<video>]... [--efficient] [--play-only-on-desktop]
                [--no-fullscreen-pause] [--low-battery N] [--seconds N] [--test-pause-after N]
Muro.Engine.exe                 # no video: follows %LOCALAPPDATA%\Muro\config.json (hot-reloaded)
Muro.Engine.exe --list-monitors # monitor index, size, DPI, stable id
Muro.Engine.exe --dump          # prints the Progman/WorkerW window tree
```

Until the library lands (M3) a wallpaper id is simply a video file path. Ctrl+C or `--seconds` exits and
restores the desktop. `MURO_MPV_LOG=1` writes `%LOCALAPPDATA%\Muro\logs\mpv.log`; `MURO_MPV_OPTS="k=v,k=v"`
overrides mpv options for benchmarking. Engine decisions are logged to `%LOCALAPPDATA%\Muro\logs\engine.log`.

## M1 findings (Win11 build 26300, Intel Iris Xe, 8 logical CPUs)

- 24H2 shell layout detected (`SHELLDLL_DefView` and `WorkerW` are both children of Progman); the classic layout is also handled.
- Hardware decode confirmed (`hevc-d3d11va`); no HEVC Store extension needed.
- CPU, whole-machine: 1080p30 about 1.5%, 1080p60 about 2.3%, 4K60 about 2.8% (about 22% of one core). Paused: 0.00%.
- Hard kill leaves the user's own wallpaper intact.

## M2 findings

- One wallpaper window and player per monitor, matched by the monitor's PnP interface path (survives
  reboots and replugging). Coordinates are physical pixels, so mixed DPI and negative-origin layouts work.
- Pause rules (`Muro.Core.PausePolicy`, unit tested): user pause, display off, session lock, full-screen app
  (borderless window covering a monitor, or `SHQueryUserNotificationState` busy/D3D/presentation on the
  foreground monitor), Battery Saver, low battery (configurable threshold), and "Play only on desktop"
  (per monitor, with the macOS settle rule so a window caught mid-animation cannot start playback).
- Events, not polling: display change, power setting, session, and WinEvent hooks for foreground, minimise,
  move and cloak. A 3 s heartbeat catches what no event announces.
- The shell replaces its `WorkerW` a moment after a display change, taking our window with it. Every
  evaluation checks the window is still alive and rebuilds it (recovery within ~3 s).
- `0x052C` is only sent when no host window exists; re-sending it makes the shell discard the current one.
- Measured on this machine: paused 0.10% of one core; 1080p60 playing about 14% of one core (1.8% of the
  CPU); Efficient (fps filter) saves GPU presents but little CPU because every frame is still decoded.
  A real 30 fps variant file comes with the transcoder in M3.
- Not testable on a one-monitor machine: a real second display and hot-plug (the resolution-change path
  exercises the same reconcile code). Not tested: Explorer restart, session lock, display sleep (no safe way to
  trigger them from a script).
