# Test library

Freely-licensed sample videos, laid out as three simulated drives so the scanner, the locations
tree, and duplicate detection all have something real to chew on. 282 MB total.

```
drive-a/Videos/Conference talks/Sintel.mp4                                 74 MB   ~14:48
drive-a/Videos/Tutorials/Graphics/Elephants Dream.mp4                      45 MB   ~10:54
drive-a/Videos/Tutorials/Graphics/Elephants Dream (theora).ogv             44 MB   ~10:54
drive-a/Videos/Tutorials/Reverse engineering/Big Buck Bunny 720 h265.mp4  5.1 MB      0:10
drive-a/Videos/Tutorials/Reverse engineering/bbb_1080_copy.mkv            4.6 MB      0:10
drive-b/Archive/2025/Big Buck Bunny 1080.mkv                              4.6 MB      0:10
drive-b/Archive/2025/Popeye - I Dont Scare.mp4                             26 MB    ~6:00
drive-c/Downloads/unsorted/Big Buck Bunny 1080 vp9.webm                   5.1 MB      0:10
drive-c/Downloads/unsorted/Jellyfish 720 vp9.webm                         1.1 MB      0:10
drive-c/Downloads/unsorted/sintel (1).mp4                                  74 MB   ~14:48
```

## What each file is there to test

- **Long clips** (Sintel, Elephants Dream, Popeye) — watch-time accumulation, the 10-second rule,
  seeking, and building up a replay heatmap. The short clips are useless for this.
- **Container / codec spread** — `.mp4` (H.264 and HEVC), `.mkv`, `.webm` (VP9), `.ogv` (Theora).
  This is the set that proves playback isn't quietly limited to H.264/MP4.
- **Deliberate duplicates**, byte-identical under different names and drives:
  - `Sintel.mp4` ≡ `sintel (1).mp4`
  - `Big Buck Bunny 1080.mkv` ≡ `bbb_1080_copy.mkv`
- **Nested subfolders** under `drive-a` — recursive scanning and the locations tree.
- **Spaces, parentheses and mixed case** in filenames — path handling.

## Sources and licences

| File | Source | Licence |
|---|---|---|
| Sintel | archive.org `Sintel` | CC BY 3.0, Blender Foundation |
| Elephants Dream | archive.org `ElephantsDream` | CC BY 2.5, Blender Foundation |
| Popeye — I Don't Scare (1956) | archive.org `popeye_i_dont_scare` | Public domain |
| Big Buck Bunny | test-videos.co.uk | CC BY 3.0, Blender Foundation |
| Jellyfish | test-videos.co.uk | Sample footage |

This directory is test material, not part of the application. Exclude it from version control.
