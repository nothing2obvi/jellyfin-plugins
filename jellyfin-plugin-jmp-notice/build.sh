#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
"${DOTNET:-dotnet}" test "$root/tests/JmpNotice.Tests.csproj" -c Release "$@"
mkdir -p "$root/output"
python3 - "$root" <<'PY'
from pathlib import Path
import sys, zipfile
root = Path(sys.argv[1])
output = root / 'output' / 'jmp-notice-0.1.0.0-jellyfin12.zip'
with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path, name in [
        (root / 'Jellyfin.Plugin.JmpNotice/bin/Release/net10.0/Jellyfin.Plugin.JmpNotice.dll', 'Jellyfin.Plugin.JmpNotice.dll'),
        (root / 'assets/jmp-notice.png', 'jmp-notice.png'),
        (root / 'meta.json', 'meta.json'),
    ]:
        archive.write(path, name)
print(output)
PY
