# Video Collection Manager

Windows desktop app for a local video collection spread across several drives. Requirements are
fixed in `REQUIREMENTS.md` — read it before changing behaviour; the "Non-requirements" section
lists things that were deliberately rejected.

## Build

```bash
dotnet run --project src/Vcm.App                    # runs on Linux and Windows alike
dotnet build src/Vcm.Cli && dotnet run --project src/Vcm.Cli -- <command>
dotnet publish src/Vcm.App -c Release -r win-x64 --self-contained true -o publish
# Never enable single-file publishing — see the note in Vcm.App.csproj. It hides libvlc's 320
# plugin DLLs inside the exe and playback dies silently while the app still starts.
```

Target is plain `net10.0`, **not** `net10.0-windows` — that is what keeps the app runnable on this
Linux box for UI iteration. Do not retarget it.

## Project boundaries

- `Vcm.Core` — SQLite, scanner, hashing, statistics. **No UI and no VLC references.** This is what
  makes the rules verifiable headlessly; keep it that way.
- `Vcm.Media` — everything libvlc. No Avalonia.
- `Vcm.App` — Avalonia UI only.
- `Vcm.Cli` — headless driver for verification.

## libvlc on Linux — hard-won details

`scripts/setup-linux-dev.sh` unpacks a VLC runtime into `.devtools/vlc-lib`. Four things are load
bearing, all in `src/Vcm.Media/VlcRuntime.cs`:

1. `Core.Initialize(path)` **throws on Linux** — it demands libvlc in `/usr/lib`. Bypassed by
   dlopening the libraries directly and registering a `DllImportResolver`.
2. The libraries must be loaded **`RTLD_GLOBAL`**. `NativeLibrary.Load` uses `RTLD_LOCAL`, which
   hides libvlccore's symbols from the plugins and segfaults the process during the plugin scan.
3. VLC 3 ignores `--plugin-path` and reads `VLC_PLUGIN_PATH`. .NET's
   `Environment.SetEnvironmentVariable` does not reach native `getenv` on Unix, so it is set
   through libc's `setenv`.
4. Plugins with unsatisfiable dependencies crash the startup scan; the setup script prunes them.
   `libavcodec`/`libavformat` cover every container we care about, MKV included.

**Never dispose a `Media` right after parsing it** — libvlc's preparser still holds it and the
process dies. `MediaReaper` parks them and releases them a few seconds later. Likewise, app exit
uses `PlayerEngine.ShutdownForExit()`, which persists the session but leaves the native objects to
the OS; disposing them during teardown segfaults.

## Data model

Identity is the **content hash** (size + first and last 1 MiB), never the path — one `videos` row
per distinct content, one `files` row per copy. That is simultaneously what makes statistics
survive a move and what produces the duplicates list.

Statistics hang off `sessions`: one row per playback, carrying `watched_ms`, `counted` and a single
`vote`. "One vote per viewing session" is therefore enforced by the schema. Watch time accrues from
the wall clock while playing, so paused time never counts. The 10-second threshold
(`PlaybackTracker.ThresholdMs`) gates both counting a view and storing a watched region.

## Verifying changes to the rules

`Vcm.Cli simulate` drives `PlaybackTracker` against a virtual clock, so the thresholds can be
checked instantly. The user's own example is the regression test: sessions of 600, 900 and 480
seconds must report exactly `33:00 total / 3 views`, and a 5-second session must not appear at all.
`playtest` does the same through real decoding.

## UI

Avalonia, dark theme, styles in `src/Vcm.App/App.axaml`. `HeatSeekBar` is hand-drawn: the replay
heat wave sits above the track, unplayed heat is dimmed, and the amber ramp is deliberately
distinct from the blue selection accent.

`dotnet run --project src/Vcm.App -- --shot <dir>` renders every page to PNG, which is how the UI
is reviewed without a live session.

Controls live **below** the video surface, not over it — libvlc renders into a native window that
Avalonia cannot reliably overlay.
