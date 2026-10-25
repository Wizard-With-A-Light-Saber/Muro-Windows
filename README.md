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

## M3: library, catalog, downloads, import (and the first UI)

**The app** (`src/Muro.App`, WinUI 3, unpackaged, self-contained):
- **Explore**: the official catalog (551 wallpapers today), search and category filter, NEW badges, download on
  demand with progress and cancel, full-screen preview through Muro own mpv (streams the short 720p loop, or
  plays the local file once downloaded). The catalog is fetched on launch and whenever the window comes to the
  front. A blocked connection shows what is wrong, never an empty grid.
- **Library**: thumbnails, Apply (all displays or one, Smooth or Efficient), single or multi-select delete with a
  confirmation, drag-and-drop or file-picker import, total size, and the download-folder controls with a cancellable
  progress dialog.
- Apply starts the engine (`Muro.Engine`) if it is not running; the engine is a separate process and keeps
  the wallpaper playing when the app is closed.

**Command line** (`muro.exe`, same code as the app): `catalog`, `download`, `import`, `library`, `delete`, `set`,
`clear`, `pause`, `resume`, `usage`, `folder`, `sweep`, `encoders`. Set `MURO_HOME` to use a throwaway library.

**Behavior carried over from the macOS app**
- library.json is edited only through `LibraryWriter` (in-process lock plus a lock file), and a damaged file is
  never overwritten.
- Delete order: off the desktop, then manifest, then files, then every list that points at it.
- Orphan sweep spares anything younger than 15 minutes and anything hidden (partial downloads).
- The download folder is a "Muro Wallpapers" folder with a marker file, joined to the library by a directory
  junction (no admin rights needed). A folder Muro did not make is never used.
- Importing the same file twice is recognised by a fingerprint of its size and first and last megabyte.

**Findings on this machine**
- Encoder detection does a real half-second test encode per candidate. Here Intel Quick Sync HEVC is chosen first;
  SVT-AV1 is the software fallback (the LGPL ffmpeg build has no x264/x265).
- The Efficient 30 fps copy takes 1080p60 from about 14% to about 8-10% of one core.
- Full-screen preview costs about 10% of one core while it plays.

**Not done / limits**
- Moving the download folder first takes the wallpapers off screen and puts them back (Windows cannot delete an
  open file). The macOS app also reacts to a drive being ejected; Windows safe-removal handling is not built yet.
- The playlist and automation stores arrive in M4; deleting a wallpaper already calls a hook for them.
- Previews still shutting down mpv from the UI thread is avoided; the engine disposes players synchronously and
  could be hardened the same way.

## M4: playlists, automations, Pause After, crossfade

**What exists**
- **Playlists**: rotate on a timer (1 to 1440 minutes), in order or shuffled. Same JSON shape as the macOS app.
- **Automations**: by timer (each wallpaper its own length, 10 s to 24 h) or by the clock (each wallpaper its own
  hours, windows may run past midnight, gaps keep what was playing). Edited on the Schedules page; the clock mode
  has a 24-hour timeline where you drag a block edge (snaps to 5 minutes; an edge shared with the next block
  takes it along; no block can be squeezed under 15 minutes or pushed over another).
- **Pause After**: global setting plus a per-wallpaper override (Library card, the gear button). Zero on a
  wallpaper means never, which is different from having no value. **Replay when the desktop is clear** restarts
  the clock each time the desktop clears.
- **Crossfade**: a change of wallpaper dissolves over 0.6 s. The outgoing frame is held as a still in a layered
  window above the new video, which fades once the new video has a frame.
- Deleting a wallpaper first removes it from every playlist and automation (a clock window's hours go to the
  window before it), stops any schedule left empty, and only then takes it off the desktop and deletes the files.
- Everything runs in the engine, so schedules keep running with the app closed. A manual Apply from the app or
  `muro set` stops the running schedule.

**CLI**: `muro playlist ...`, `muro automation ...`, `muro schedule`, `muro pause-after ...` (see `muro --help`).

**Measured on this PC** (Win11 build 26300)
- Timer steps of 10 s switch on time; the running step is persisted in `schedule.json`.
- Pause After 5 s froze the wallpaper at 5 s (log: `paused (PauseAfter)`); a wallpaper set to never kept playing.
- Crossfade: screen luminance ramps 88 to 130 in about 16 steps over about 0.5 s; before it, it was a hard cut
  (it never flashed black, since the old frame stays until the new one is ready).
- Real mouse drag on the timeline: the 08:00 edge moved +155 minutes, both neighbours followed, Save wrote it.

**Defaults I chose (nobody was available to ask; change any of them)**
- Starting a schedule while nothing is on screen shows the first step at once; stopping a schedule restores what was
  on the desktop before it started (if that wallpaper still exists).
- A schedule drives every display, so per-display assignments give way to it while it runs.
- A playlist that slept through several intervals advances once, not once per missed interval (as macOS).
- A clock schedule started while nothing is playing and inside a gap shows the last window to end.
- Timeline edges snap to 5 minutes; the timeline edits edges only. Reordering the list moves wallpapers between
  slots (hours stay with the rows), as on macOS. Dragging a whole block is not built.
- Block colours follow the position in the list.

**Not done in M4 (with reasons)**
- The **lock screen** and **screen saver** playlists run through the same scheduler and state file
  (`schedule.json` already has a slot for each), but nothing applies them until M6.
- The tray **play / replay** button that restarts Pause After is M5; the engine already has `Replay()`.
- A stray `paused (Fullscreen)` appeared once while a heavy screenshot loop ran; it did not reproduce in a
  controlled run and I could not find the cause.
