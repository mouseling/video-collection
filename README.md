# Video Collection Manager

A Windows desktop app for a video collection spread across several drives. Register each folder
once and the app becomes the single place to browse, play, tag and rank everything — no more
re-navigating the filesystem to find something to watch.

Full behaviour is specified in [REQUIREMENTS.md](REQUIREMENTS.md).

## What it does

- **Locations** — registered root folders mirrored as the real on-disk tree, scanned recursively.
- **Playback** — plays video itself through libvlc, so arbitrary containers and codecs work.
  Always starts from the beginning; resume-where-you-left-off is deliberately not offered.
- **Play queue** — starting a video from a folder or a tag queues the rest of that context.
- **Tags** — add while watching, create on the fly or reuse; browse everything under one tag.
- **Like / dislike** — one vote per viewing session, accumulating over time. Sortable four ways.
- **Most viewed** — total watch time and view count, with paused time excluded and sessions under
  ten seconds ignored. Hover a row for the per-view breakdown.
- **Most viewed parts** — the stretches actually watched build a replay heatmap drawn on the seek
  bar, so a passage watched three times reads hotter than one watched once.
- **Duplicates** — identical content at several paths, found by hash, listed for manual cleanup.

## Layout

| Project | What it is |
|---|---|
| `src/Vcm.Core` | Database, scanner, content hashing, statistics rules. No UI, no VLC — testable headlessly. |
| `src/Vcm.Media` | libvlc: runtime loading, metadata probing, the player engine. No UI framework. |
| `src/Vcm.App` | Avalonia desktop application. |
| `src/Vcm.Cli` | Headless driver used to exercise and verify the core. |
| `prototype/` | The original look-and-feel mockup, kept for reference. |

## Build and run

```bash
dotnet run --project src/Vcm.App
```

Ship a Windows build:

```bash
dotnet publish src/Vcm.App -c Release -r win-x64 --self-contained true -o publish
```

Produces a ~207 MB folder: `VideoCollectionManager.exe` plus `libvlc/win-x64/` holding libvlc and
its 320 plugins. **Do not turn on single-file publishing** — it pulls those plugin DLLs inside the
executable, where libvlc cannot find them, and the app then starts but can decode nothing. The
project pins `PublishSingleFile` to false so this is hard to do by accident.

## Installing and updating

There is **no installer**. Download `…-Portable.zip` from the
[Releases page](https://github.com/mouseling/video-collection/releases), unzip it anywhere — a
disk, a stick, wherever — and run `VideoCollectionManager.exe`. Nothing is written to the registry
and nothing lands in Program Files.

It still updates itself. On launch it asks GitHub whether a newer release exists; if so an
**Update to x.y.z** button appears at the bottom of the sidebar. Press it and the app downloads the
difference, restarts, and comes back on the new version. Velopack ships deltas, so after the first
download later updates transfer only what actually changed rather than the full ~207 MB.

Being offline is not an error: the check fails silently and the app carries on.

### Where the library lives

| Running as | Database |
|---|---|
| Portable release | `library.db` in the unzipped folder, beside `Update.exe` |
| Dev build / plain publish | `%AppData%\VideoCollectionManager\library.db` |

The portable database sits in the folder root rather than next to the executable on purpose:
updates replace the inner `current\` directory wholesale, so anything stored beside the exe would
be destroyed on every update. Keeping it in the root means your ratings and watch history survive
updates *and* travel with the folder if you move it.

### Cutting a release

Releases are built by `.github/workflows/release.yml` on a Windows runner — Velopack cannot
produce Windows packages from Linux. Push a tag:

```bash
git tag v0.1.1 && git push origin v0.1.1
```

One-time setup: add a **read-only** PAT as the `UPDATER_TOKEN` repository secret
(Settings → Secrets and variables → Actions). The build bakes it into the binary so the shipped
app can query this private repo's releases. Without it the app builds and runs but never sees an
update. For local release builds, put the same value in an untracked `updater.token` at the
repository root.

### Developing on Linux

The app targets plain `net10.0`, so it builds and runs on Linux as well as Windows. Windows gets
libvlc from the `VideoLAN.LibVLC.Windows` package automatically; Linux needs a one-time local
runtime, since installing VLC system-wide requires root:

```bash
./scripts/setup-linux-dev.sh     # unpacks a VLC runtime into .devtools/
./scripts/fetch-testdata.sh      # ~280 MB of open-licensed sample videos
```

## Verifying the core

`Vcm.Cli` drives the library without the UI, which is how the statistics rules were checked:

```bash
export VCM_DB=/tmp/vcm-test.db
dotnet run --project src/Vcm.Cli -- add-root testdata/drive-a
dotnet run --project src/Vcm.Cli -- scan
dotnet run --project src/Vcm.Cli -- probe

# Fake sessions against a virtual clock: 10 + 15 + 8 minutes should total 33:00 over 3 views,
# while a 5-second start must not count at all.
dotnet run --project src/Vcm.Cli -- simulate 1 5   0  0
dotnet run --project src/Vcm.Cli -- simulate 1 600 0  1
dotnet run --project src/Vcm.Cli -- viewed time
dotnet run --project src/Vcm.Cli -- heat 1 72      # replay heat as an ASCII bar

# Real decoding end to end, then report what the tracker persisted.
dotnet run --project src/Vcm.Cli -- playtest 1 14
```

Run `dotnet run --project src/Vcm.Cli` with no arguments for the full command list.

## Design notes

**Videos are identified by content, not by path.** A hash of the file size plus its first and last
megabyte gives one `videos` row per distinct piece of content and one `files` row per copy on disk.
Ratings and watch history therefore survive renames and moves, and duplicate copies fall out of the
same structure for free. Hashing 280 MB takes under a tenth of a second because the middle of the
file is never read.

**Statistics live on sessions.** Each playback inserts a `sessions` row carrying its watch time and
its single vote, which is what makes "one vote per viewing session" a property of the schema rather
than a rule the UI has to remember. Watch time accrues from the wall clock only while playback is
actually running, so pausing does not inflate it.

**Controls sit below the video, not over it.** libvlc renders into a native window that Avalonia
cannot reliably draw on top of. The control bar is therefore a strip beneath the video surface.
