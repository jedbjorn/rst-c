#!/usr/bin/env bash
# rst-c's own: SC-048 item 2. Rebuild the notification icon with librsvg and ImageMagick.
# Usage: bash build/generate-tray-icon.sh [preview-directory]
# Each entry is rendered from the vector at its real size, without fonts or a resized 48 px image.
set -euo pipefail
export LC_ALL=C
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
icon_tmp=$(mktemp -d)
trap 'rm -rf -- "$icon_tmp"' EXIT
sizes=(16 20 24 32 40 48)
frames=()
for size in "${sizes[@]}"; do
    rsvg-convert -w "$size" -h "$size" "$repo_root/installer/branding/rst-tray.svg" -o "$icon_tmp/$size.png"
    frames+=("$icon_tmp/$size.png")
done
magick "${frames[@]}" -strip -type TrueColorAlpha "$repo_root/installer/branding/rst-tray.ico"

if [[ $# -gt 0 ]]; then
    mkdir -p -- "$1"
    for size in 16 32; do
        for theme in dark light; do
            background='#202020'
            if [[ $theme == light ]]; then background='#F3F3F3'; fi
            magick -size "$((size * 3))x$((size * 3))" "xc:$background" \
                "$icon_tmp/$size.png" -gravity center -composite -strip "$1/rst-tray-$size-$theme.png"
        done
    done
fi
