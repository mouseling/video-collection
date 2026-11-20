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

## Releases and self-update

Velopack, same shape as the radar. Three rules:

1. **Windows packages can only be built on Windows.** The Linux `vpk` build exposes nothing but
   the AppImage packer, so `.github/workflows/release.yml` runs on `windows-latest`. Don't try to
   pack a release locally.
0. **Every push to `main` touching `src/` ships a release**, versioned by nbgv from commit height
   — the same model as the radar. Treat merging to `main` as shipping to users. An explicit
   `vX.Y.Z` tag overrides the computed version when one is pushed.
2. **Portable is still self-updating.** Velopack's `IsInstalled` means "running from a Velopack
   layout", not "installed by Setup.exe" — the portable zip qualifies. `IsPortable` is the separate
   flag. We ship only the portable zip; there is deliberately no installer.
3. **Never store data beside the executable.** Updates replace `current\` wholesale. `AppPaths`
   puts `library.db` in the Velopack app root (next to `Update.exe`) so it survives updates and
   travels with the folder.

The repo is **public**, deliberately, so the updater needs no credentials at all. The private-repo
alternative would mean embedding a read-only PAT in the shipped binary, recoverable by anyone
holding it, and silently 404ing on every check whenever it is missing or misscoped. If the repo
ever goes private, that plumbing has to come back — see the radar's `GenerateUpdaterSecrets`
target for the pattern.

`VelopackApp.Build().Run()` must stay the first statement in `Main`, ahead of any Avalonia call —
it is what finishes an update and re-launches.

The release workflow fails the build if `libvlc/win-x64` carries fewer than 2 core DLLs or 100
plugins. That guard exists because single-file publishing silently hides them inside the exe and
the app then starts but decodes nothing — a failure mode that looks fine until someone presses
play.

## Concurrency

Four threads touch this app: the Avalonia UI thread, a background scan, a 250 ms player tick on a
threadpool thread, and libvlc's own event threads. Two rules keep that honest.

**All SQL goes through `Database`'s helpers.** The connection, the gate and `Cmd` are private so
nothing can run a statement outside the lock. Never expose them again — `PlayerEngine` used to hold
the raw connection via `Library.Db`, which made the lock a convention rather than a guarantee.

**Insert-then-read-the-id must be one statement.** Use `Database.InsertReturningId`, never
`INSERT` followed by a separate `SELECT last_insert_rowid()`: the gate is released between the two,
the rowid belongs to the *connection*, and a concurrent insert on another thread hands back its id.
That corrupted session↔video linkage whenever a scan overlapped playback.

**`PlayerEngine` serialises its own state** on one lock. Every queue advance has the libvlc event
thread closing a session while the tick thread is still updating the same tracker.

## Data model

Identity is the **content hash** (size + first and last 1 MiB), never the path — one `videos` row
per distinct content, one `files` row per copy. That is simultaneously what makes statistics
survive a move and what produces the duplicates list.

Statistics hang off `sessions`: one row per playback, carrying `watched_ms`, `counted` and a single
`vote`. "One vote per viewing session" is therefore enforced by the schema. Watch time accrues from
the wall clock while playing, so paused time never counts. The 10-second threshold
(`PlaybackTracker.ThresholdMs`) gates both counting a view and storing a watched region.

## Verifying changes to the rules

`dotnet test` first — `tests/Vcm.Core.Tests` covers the statistics rules, scanner reconciliation
and hashing, and runs in under a second. CI (`.github/workflows/ci.yml`) runs it on every push.

Two tests exist specifically because the bugs they catch were real and silent, so do not weaken
them: `A_root_that_has_vanished_does_not_empty_the_library` (the delete sweep must stay scoped to
roots that were actually walked) and `A_short_session_leaves_no_region_even_if_the_position_travelled_far`
(regions are judged on watched time, never on how far the media position moved). Both were
confirmed to fail against the old code before the fixes landed.

`The_hash_is_stable_for_identical_content` pins the content-hash algorithm. If it fails, stop:
changing that hash orphans every stored video and silently erases the user's entire history on
the next scan.

`Vcm.Cli playtest` remains the manual gate for anything involving real decoding, which the unit
tests deliberately do not attempt.

## UI

Avalonia, dark theme, styles in `src/Vcm.App/App.axaml`. `HeatSeekBar` is hand-drawn: the replay
heat wave sits above the track, unplayed heat is dimmed, and the amber ramp is deliberately
distinct from the blue selection accent.

`dotnet run --project src/Vcm.App -- --shot <dir>` renders every page to PNG, which is how the UI
is reviewed without a live session.

Controls live **below** the video surface, not over it — libvlc renders into a native window that
Avalonia cannot reliably overlay.
