# UI prototype (Avalonia)

A look-and-feel mockup of the video collection manager, built to answer one question: **does
Avalonia look right for this app?** Everything is mock data — no database, no scanning, no
playback. Only the visuals and the custom seek bar are real.

## Run it

```bash
cd prototype
dotnet run
```

It opens on the local X display. If it complains about a display, prefix it:

```bash
DISPLAY=:1 dotnet run
```

## Regenerate the screenshots

```bash
DISPLAY=:1 dotnet run -- --shot ../shots
```

Renders every page to `../shots/*.png` via `RenderTargetBitmap`, so the UI can be reviewed
without a live session.

## What to look at

| Page | Shows |
|---|---|
| Now playing | Video surface with the control bar **below** it, not overlaid |
| Locations | Root folders mirrored as a real on-disk tree |
| Tags | Tag chips with counts, filtered file list |
| Rating | Accumulated likes/dislikes, 4-way sort switch |
| Most viewed | Total time + view count, 2-way sort switch, hover tooltip on rows |
| Duplicates | Same-hash groups across drives |

The seek bar (`HeatSeekBar.cs`) is the only hand-written control: it draws the "most viewed
parts" heat wave, dims the unplayed portion, and has a cold→hot legend at the right of the
control bar. Click anywhere on it to move the playhead.

## Things deliberately not addressed yet

Window chrome, icons, keyboard shortcuts, light theme, and the add-path dialog. The colour
palette and density are the parts worth reacting to.
