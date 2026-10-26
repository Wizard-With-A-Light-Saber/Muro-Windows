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
- The tray **play / replay** button that restarts Pause After came in M5 ("Play again" in the tray menu).
- A stray `paused (Fullscreen)` appeared once while a heavy screenshot loop ran; it did not reproduce in a
  controlled run and I could not find the cause.

**Checked through the real UI on this PC (throwaway library):** Play and Stop on a playlist (the engine starts
by itself, takes over, and Stop puts the previous wallpaper back); dragging a block edge on the timeline,
including the last block end at 24:00; the playlist editor dialog; saving an automation.

**Not tested, please check:**
- Building a *timer* automation from scratch in the editor (only the clock mode was exercised through the UI).
- Creating a playlist from scratch in the dialog, and the Shuffle switch there.
- The per-wallpaper Pause After menu (the gear button on a Library card); the same rule was tested from the CLI.
- Pause After re-arming after an unlock (Win+L) or display wake, and "Replay when the desktop is clear" end to end.
- Schedule catch-up after the PC sleeps and resumes, or after a clock or time-zone change.
- A playlist rotating on its own across several intervals (only 1 and 10 second timer steps were run live).
- Per-display behaviour of schedules on a second monitor.

**"Drag blocks"** is built as dragging a block *edge* (and the list order). Dragging a whole block sideways is not
built; say if you want it.

## M5: tray, Settings, Start with Windows, update checker, Featured

**What exists**
- **Tray icon**, owned by the engine (so it is there with the app closed). Left click opens Muro, or brings it
  to the front. Right click: Open Muro, Pause / Resume wallpaper, Play again (restarts Pause After), Stop
  "<schedule>" while a playlist or automation runs, Quit Muro. The icon comes back after an Explorer restart and
  is removed when the engine quits. It can be turned off in Settings.
- **Settings** (the gear at the bottom of the navigation): Start with Windows, tray icon, playback speed (0.5x to
  1.5x, now actually applied by the engine: it was a config field that nothing read), default quality, the four
  energy rules (covered by a full-screen app, Battery Saver, low battery with its percentage, play only on the
  desktop), the connected displays, library size and a button to open the Muro folder, and About with
  *Check for updates*.
- **Start with Windows** writes `HKCU\...\Run\Muro` pointing at `Muro.Engine.exe` and nothing else: Muro starts
  quietly with its tray icon and plays the wallpaper; the app window opens from the tray. If you switch it off in
  Windows (Settings, Apps, Startup, or Task Manager) the switch shows Off and says why; switching it on here
  allows it again.
- **Update checker**: reads the GitHub releases of `MrRockySL/Muro` on launch, whenever the window comes to the
  front (at most every 6 hours), and on demand. A newer release shows a banner (once per version) with *What's
  new*, a dialog with the release notes and a Download button. The automatic check is silent when it fails; a
  manual one says so.
- **Featured** (first tab, the macOS Home): a banner, a strip to choose which downloaded wallpaper it shows, the
  **Latest drop** (everything published at the newest publish time) and **Muro's Pick** (12 at random, drawn once
  a day, never the drop, remembered for the day).
- The engine is now a windowless exe (`WinExe`) with the Muro icon, so a sign-in launch opens no console. It
  attaches to the console that started it for the harness switches; in a plain console window the prompt can
  come back before the output, so use `| more` or `Start-Process -Wait -NoNewWindow`.

**Defaults I chose (nobody was available to ask; change any of them)**
- **Update rule.** The macOS release (a .dmg, version 5.x) lives in the same repository, so `releases/latest` would
  offer it to Windows forever. Instead the newest non-draft, non-prerelease release that has a **Windows asset**
  wins: an `.exe` or `.msi`, or a `.zip` whose name contains `win` (the installer is preferred). A release without
  one is not an update. Publish Windows builds as GitHub releases with such an asset and a version above the
  current one (`Directory.Build.props` says 0.1.0). Nothing is installed automatically: Download opens the asset in
  the browser. Only the app checks; the tray does not.
- **Quit Muro** in the tray stops the wallpaper (the engine exits and your own wallpaper returns). Applying a
  wallpaper, or Open Muro then Apply, starts it again.
- The tray icon is **on** by default; Start with Windows is **off**.
- **Default quality** only decides how Featured's Apply plays; the Library keeps its Smooth / Efficient menu.
- **Pause After** and *Replay when the desktop is clear* stay on the Schedules page (one place, so two controls
  cannot disagree); the download-folder controls stay on the Library page.
- The Featured banner follows the macOS rules: only wallpapers you downloaded, never your own imports, never a
  stream. There is no bundled wallpaper on Windows, so before the first download it shows the newest catalog
  wallpaper as a still picture with a Download button. The banner is a still image, not a looping video.
- Every settings change reads `config.json`, changes one field and saves at once, because the schedule runner
  writes that file too. UI-only choices (default quality, banner choice, today's Pick, the seen release) are in
  `app-settings.json`. An older Muro build left a `settings.json` in `%LOCALAPPDATA%\Muro` (different fields):
  Muro does not read or write it.
- The window icon and tray icon are made from the macOS app icon by `scripts/make-icon.ps1`.
- **A busy or damaged `config.json` is never overwritten.** Reading is tried a few times first (a virus scanner or
  the engine's own file watcher may hold the file for a moment); if it still cannot be read, `Save` refuses
  instead of writing defaults over your wallpaper assignment, and Settings says the setting was not saved. Writes
  go to a temp file with its own name and the rename is retried briefly. `app-settings.json` follows the same rule.
- **The tray icon retries.** If Windows refuses it while the shell is still starting (a launch at sign-in can beat
  it), it is added again every 2 seconds for about a minute, and again whenever Explorer restarts.

**Not done in M5 (with reasons)**
- Appearance (Default / Light / Dark): the app follows the Windows theme.
- *Clear library and cache*: it deletes data, and doing it safely needs the playing and scheduled wallpapers
  excluded; not worth guessing at.
- Screen saver delay and lock-screen settings: M6.
- The menu-bar icon styles and Dock icon options have no Windows meaning.
- Update download and install inside the app (the installer is M7).

**Checked live on this PC (throwaway library, a local fake release feed)**
- Tray: icon appears, menu opens, Pause writes `paused` and the engine logs `paused (User)`, Play again wakes a
  wallpaper Pause After froze, Quit exits and removes the icon, Open Muro starts the app when it is closed, a click
  on the icon restores a minimised window.
- Settings: tray, desktop-only and speed each change exactly one field in `config.json`; the engine logs
  `speed 1.5x`. Start with Windows wrote the Run value (the engine path) and removed it again.
- Updates: with the feed serving a macOS 5.2 release first and a Windows 9.9.0 second, the banner offered 9.9.0;
  What's new showed the parsed notes; Later retired the banner and stored the version. Against real GitHub the
  check answers "up to date" (there is no Windows release yet).
- Featured: banner from the library, choosing another wallpaper from the strip, Apply (starts the engine), the
  drop and Pick rows, and the Pick being saved for the day.

**Not tested, please check:**
- Start with Windows through a real sign-in. I never rebooted; I only wrote and removed the registry value.
  Test it with the **installed** build, not a `bin\Release` one: the value stores the engine path it was set from.
- Switching Start with Windows off in Task Manager and back on in Settings.
- The tray after a real Explorer restart. I posted the `TaskbarCreated` message to the tray window and the icon
  stayed listed exactly once, but I did not restart Explorer. The retry after a *failed* first add (a sign-in
  race) was not exercised either: I could not make Windows refuse the add.
- The config-file protections are covered by unit tests (file held open, damaged file, short lock), not by a real
  virus scanner racing the engine.
- The tray menu on a high-DPI second monitor, and keyboard use of the tray (Windows decides how that works).
- *Download* in the update dialog (it opens the asset URL in the browser; the test feed pointed at example.com).
- The hero's Efficient apply when a 30 fps copy has to be made first (progress bar on the banner).
- Playback speed by eye (the engine logs the change and mpv accepted it; I did not time a clip).
- Dark/light theme looks, and narrow window widths, of the new pages.

## M6: screen saver and lock screen

**What exists**
- **Screen saver** (`Muro.scr`, built by `src/Muro.ScreenSaver`): plays the chosen wallpaper on every display and ends
  on the first key, click, wheel turn or mouse movement of more than about 10 pixels (a small jiggle does not end
  it). It is a normal Windows screen saver, so Windows' own idle timer starts it. Settings, *Screen saver*:
  **Use Muro as the screen saver** points Windows' per-user setting (`SCRNSAVE.EXE`, no admin rights) at
  `Muro.scr` and remembers what was there, so turning it off puts back exactly the old program, on/off state and
  delay. **Start after** is Windows' own delay. **Plays**: the desktop wallpaper (the default), one wallpaper, a
  playlist or an automation (the engine runs it for the screen saver exactly like the desktop one). **Try it now**.
- **Lock screen**: Windows cannot play video on the lock screen, so Muro sets one **still frame** of the chosen
  wallpaper (up to 3840 wide, made once by ffmpeg into `Stills\`). A playlist or automation changes the picture as
  it moves on. The first time, your own lock screen picture is saved; turning it off, or deleting the wallpaper,
  puts it back. No admin rights or package identity are needed.
- The playlist, automation and Pause After machinery now has one runner per surface (desktop, screen saver, lock
  screen), each with its own state in `schedule.json` (the slots M4 left).
- Deleting a wallpaper also clears it from the screen saver and lock screen and removes its still.
- Fixed on the way: mpv keeps the PC awake while a video plays by default, which would have stopped Windows'
  screen saver and display sleep for as long as the wallpaper played. It is now switched off in the player.

**Defaults I chose (nobody was available to ask; change any of them)**
- The screen saver plays the desktop wallpaper unless you pick something else; with no library it ends at once.
- Turning Muro on as the screen saver keeps Windows' current delay (20 minutes here) or uses 10 minutes if the
  screen saver was off.
- The lock screen still is the frame at about 2 seconds in (or the middle of a short clip).
- Only a lock screen set to **Picture** is replaced cleanly. If it was Windows Spotlight or a slideshow, Muro
  switches it to a picture, says so in Settings, and turning it off restores the saved picture, not Spotlight.
- The screen saver's small preview in Windows' own dialog is left blank, and its Settings button shows a message
  that points to Muro.

**Checked live on this PC (throwaway library)**
- The screen saver on both displays, over everything, no pointer; ended by key, click, wheel and movement, not by a
  2 px jiggle; Windows launched `Muro.scr /s` by itself (its log shows it) when asked to start the screen saver.
- Settings: on writes `SCRNSAVE.EXE` and keeps the 1200 s delay; Start after 1 minute is written to Windows;
  off restored the exact original values (no program, active, 1200 s).
- An automation chosen for the screen saver rotated its wallpaper every 10 s through the engine.
- The lock screen: on changed the picture (440 KB against the original 773 KB), off brought back a picture with a
  pixel difference of 0 from the original.

**Not tested, please check:**
- Windows starting the screen saver on its own after real idle time (I could not stay idle: the PC is in use).
  Triggering it by message did start `Muro.scr /s`, but that run found the real library empty and ended.
- The lock screen itself (press Win+L and look). I only read the stored picture back, never looked at the real
  lock screen.
- A lock screen set to Spotlight or a slideshow, and Windows policies that manage the lock screen.
- Several lock screen changes in a row (a playlist) on a slow PC, and a long wallpaper whose still takes time.
- Multi-monitor with different resolutions for the screen saver (both displays here are 1920x1080).
- The Settings button in Windows' screen saver dialog, and *Try it now* from the app.
- `muro` has no `--on screensaver|lockscreen` switch yet: the screen saver and lock screen are set from Settings.
- Installed layout: `Muro.scr` and the engine's ffmpeg lookup are only exercised in the development tree (M7).
