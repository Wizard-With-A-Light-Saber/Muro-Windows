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

## M1 harness

```powershell
src\Muro.Engine\bin\Release\net8.0-windows10.0.19041.0\Muro.Engine.exe <video.mov> [--seconds N] [--pause-after N]
Muro.Engine.exe --dump     # prints the Progman/WorkerW window tree
```

Ctrl+C or `--seconds` exits and restores the desktop. `MURO_MPV_LOG=1` writes `%LOCALAPPDATA%\Muro\logs\mpv.log`;
`MURO_MPV_OPTS="k=v,k=v"` overrides mpv options for benchmarking.

## M1 findings (Win11 build 26300, Intel Iris Xe, 8 logical CPUs)

- 24H2 shell layout detected (`SHELLDLL_DefView` and `WorkerW` are both children of Progman); the classic layout is also handled.
- Hardware decode confirmed (`hevc-d3d11va`); no HEVC Store extension needed.
- CPU, whole-machine: 1080p30 about 1.5%, 1080p60 about 2.3%, 4K60 about 2.8% (about 22% of one core). Paused: 0.00%.
- Hard kill leaves the user's own wallpaper intact.
