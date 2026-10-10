#!/usr/bin/env bash
# Fetches Cavern 2.1 (https://github.com/VoidXH/Cavern) into decoder/cavern/src and applies the
# linux-atmos CPU patch (joc-cpu.patch). AtmosDecode.csproj builds against this copy.
set -euo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/src"
TAG=2.1
COMMIT=6fa1a0f73560efbfc759c1d9e530a059a0d95a43   # what tag 2.1 pointed to when the patch was made

if [[ -f $SRC/.linux-atmos-patched ]] && [[ $(cat "$SRC/.linux-atmos-patched") == "$(sha256sum < "$HERE/joc-cpu.patch")" ]]; then
    exit 0
fi
rm -rf "$SRC"
git -c advice.detachedHead=false clone -q --depth 1 --branch "$TAG" https://github.com/VoidXH/Cavern "$SRC"
[[ $(git -C "$SRC" rev-parse HEAD) == "$COMMIT" ]] || { echo "get-cavern: tag $TAG moved, check joc-cpu.patch" >&2; exit 1; }
git -C "$SRC" apply "$HERE/joc-cpu.patch"
sha256sum < "$HERE/joc-cpu.patch" > "$SRC/.linux-atmos-patched"
