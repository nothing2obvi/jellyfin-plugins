#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "$0")" && pwd)"
build_target() {
    local abi framework version suffix
    case "$1" in
        10.11) abi=10.11.0.0; framework=net9.0; version=0.1.1.0; suffix=jellyfin10.11 ;;
        12) abi=12.0.0.0; framework=net10.0; version=0.2.1.0; suffix=jellyfin12 ;;
        *) echo 'Usage: bash ArtistPruner/build.sh [all|10.11|12]' >&2; exit 2 ;;
    esac
    "${DOTNET:-dotnet}" build "$root/Jellyfin.Plugin.ArtistPruner" -c Release -p:JellyfinTargetAbi="$abi"
    python3 - "$root" "$abi" "$framework" "$version" "$suffix" <<'PY'
import json, pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
abi, framework, version, suffix = sys.argv[2:]
out = root / 'dist'
out.mkdir(exist_ok=True)
manifest = {
    'guid': 'e8b5f1a3-7c9d-4e2b-8a1f-3d6c5e4b9a0f',
    'name': 'Artist Pruner', 'version': version, 'targetAbi': abi,
    'overview': 'Preview and prune extra track and album artist credits',
    'description': 'Test release with optional full mode, validated recovery points, and restore. No artist records or music files are deleted.',
    'owner': 'nothing2obvi', 'category': 'Music', 'imagePath': 'artist-pruner.png'
}
package = out / f'artistpruner-{version}-{suffix}.zip'
with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
    archive.write(root / f'Jellyfin.Plugin.ArtistPruner/bin/Release/{framework}/Jellyfin.Plugin.ArtistPruner.dll', 'Jellyfin.Plugin.ArtistPruner.dll')
    archive.write(root / f'Jellyfin.Plugin.ArtistPruner/bin/Release/{framework}/TagLibSharp.dll', 'TagLibSharp.dll')
    for notice in ['THIRD-PARTY-NOTICES.md', 'TagLibSharp-LICENSE.txt']:
        archive.write(root / notice, notice)
    archive.writestr('meta.json', json.dumps(manifest, indent=2))
    archive.write(root / 'LICENSE', 'LICENSE')
    archive.write(root / 'assets/artist-pruner.png', 'artist-pruner.png')
print(package)
PY
}
case "${1:-all}" in
    all) build_target 10.11; build_target 12 ;;
    *) build_target "$1" ;;
esac
