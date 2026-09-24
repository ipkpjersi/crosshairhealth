#!/usr/bin/env bash
# Builds CrosshairHealth.net.dll, which draws the target health ring back while C06alt's
# First Person Mod is providing the view. See TODO.md item 13.
#
# Needs mono-devel for mcs. The ScriptHookDotNet reference assembly ships inside the
# LCPDFR install, so LCPDFR must already be installed before building.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Where GTA IV is installed. Pass it as the first argument, or set GAME_ROOT. The default
# walks Steam's own library list, so no drive layout is written down here.
find_game_root() {
    local vdf root path
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.steam/debian-installation/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [ -f "$vdf" ] || continue
        while IFS= read -r path; do
            for root in "$path/steamapps/common/Grand Theft Auto IV - Original Edition/GTAIV" \
                        "$path/steamapps/common/Grand Theft Auto IV/GTAIV"; do
                [ -d "$root" ] && { echo "$root"; return 0; }
            done
        done < <(sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf")
    done
    return 1
}

GAME_ROOT="${1:-${GAME_ROOT:-$(find_game_root || true)}}"
[ -n "$GAME_ROOT" ] || { echo "error: GTA IV not found; pass the GTAIV folder as the first argument" >&2; exit 1; }
REF="$GAME_ROOT/LCPDFR/API Example/References/ScriptHookDotNet.dll"

if [[ ! -f "$REF" ]]; then
    echo "ScriptHookDotNet reference assembly not found at: $REF" >&2
    exit 1
fi

mcs -target:library -platform:x86 -sdk:4.5 \
    -out:"$HERE/CrosshairHealth.net.dll" \
    -r:"$REF" -r:System.Drawing.dll -r:System.Windows.Forms.dll \
    "$HERE/CrosshairHealth.cs"

echo "Built: $HERE/CrosshairHealth.net.dll"
echo "Install with: cp '$HERE/CrosshairHealth.net.dll' '$GAME_ROOT/scripts/'"
