#!/usr/bin/env bash
# Unpacks a VLC runtime into .devtools/ so the app can run on this Linux dev box.
#
# Windows gets libvlc from the VideoLAN.LibVLC.Windows NuGet package automatically; Linux has no
# such package, and installing VLC system-wide needs root. Extracting the Ubuntu packages into a
# local directory sidesteps both. See src/Vcm.Media/VlcRuntime.cs for how it is loaded.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="$ROOT/.devtools"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

V=3.0.20-3build6
BASE=http://archive.ubuntu.com/ubuntu/pool/universe/v/vlc

echo "Downloading VLC $V ..."
for p in libvlc5_${V}_amd64 libvlccore9_${V}_amd64 \
         vlc-plugin-base_${V}_amd64 vlc-plugin-video-output_${V}_amd64 vlc-data_${V}_all; do
  curl -sL --fail -o "$WORK/${p}.deb" "$BASE/${p}.deb"
  echo "  ${p}.deb"
done

echo "Extracting ..."
mkdir -p "$WORK/root"
for d in "$WORK"/*.deb; do dpkg -x "$d" "$WORK/root"; done

rm -rf "$DEST/vlc-lib"
mkdir -p "$DEST"
cp -r "$WORK/root/usr/lib/x86_64-linux-gnu" "$DEST/vlc-lib"

# Plugins whose dependencies are not installed crash libvlc's startup scan. Drop them; libavcodec
# and libavformat already cover every container the app cares about.
LIB="$DEST/vlc-lib"
export LD_LIBRARY_PATH="$LIB:$LIB/vlc"
pruned=0
while IFS= read -r so; do
  if ldd "$so" 2>/dev/null | grep -q "not found"; then rm -f "$so"; pruned=$((pruned+1)); fi
done < <(find "$LIB/vlc/plugins" -name '*_plugin.so')

echo "Done. Pruned $pruned unsatisfiable plugins, kept $(find "$LIB/vlc/plugins" -name '*_plugin.so' | wc -l)."
echo "Runtime: $LIB"
