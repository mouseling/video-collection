#!/usr/bin/env bash
# Downloads open-licensed sample videos into testdata/, laid out as three simulated drives.
# See testdata/README.md for what each file exercises.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="$ROOT/testdata"

mkdir -p "$BASE/drive-a/Videos/Conference talks" \
         "$BASE/drive-a/Videos/Tutorials/Graphics" \
         "$BASE/drive-a/Videos/Tutorials/Reverse engineering" \
         "$BASE/drive-b/Archive/2025" \
         "$BASE/drive-c/Downloads/unsorted"

dl() { [ -f "$2" ] && { echo "  have $(basename "$2")"; return; }
       curl -sL --fail -o "$2" "$1" && echo "  got  $(basename "$2")"; }

A=https://archive.org/download
T=https://test-videos.co.uk/vids

dl "$A/Sintel/sintel-2048-stereo_512kb.mp4"               "$BASE/drive-a/Videos/Conference talks/Sintel.mp4"
dl "$A/ElephantsDream/ed_1024_512kb.mp4"                  "$BASE/drive-a/Videos/Tutorials/Graphics/Elephants Dream.mp4"
dl "$A/ElephantsDream/ed_1024.ogv"                        "$BASE/drive-a/Videos/Tutorials/Graphics/Elephants Dream (theora).ogv"
dl "$A/popeye_i_dont_scare/popeye_i_dont_scare_512kb.mp4" "$BASE/drive-b/Archive/2025/Popeye - I Dont Scare.mp4"
dl "$T/bigbuckbunny/mkv/1080/Big_Buck_Bunny_1080_10s_5MB.mkv"       "$BASE/drive-b/Archive/2025/Big Buck Bunny 1080.mkv"
dl "$T/bigbuckbunny/webm/vp9/1080/Big_Buck_Bunny_1080_10s_5MB.webm" "$BASE/drive-c/Downloads/unsorted/Big Buck Bunny 1080 vp9.webm"
dl "$T/bigbuckbunny/mp4/h265/720/Big_Buck_Bunny_720_10s_5MB.mp4"    "$BASE/drive-a/Videos/Tutorials/Reverse engineering/Big Buck Bunny 720 h265.mp4"
dl "$T/jellyfish/webm/vp9/720/Jellyfish_720_10s_1MB.webm"           "$BASE/drive-c/Downloads/unsorted/Jellyfish 720 vp9.webm"

# Byte-identical copies under different names, to exercise hash-based duplicate detection.
cp -n "$BASE/drive-a/Videos/Conference talks/Sintel.mp4" "$BASE/drive-c/Downloads/unsorted/sintel (1).mp4"
cp -n "$BASE/drive-b/Archive/2025/Big Buck Bunny 1080.mkv" \
      "$BASE/drive-a/Videos/Tutorials/Reverse engineering/bbb_1080_copy.mkv"

echo "Done: $(du -sh "$BASE" | cut -f1) in $BASE"
