#!/bin/bash
# Ships the working tree (src, tests-free build inputs) to the Windows PC as one archive and builds a JIT engine there.
# Usage (from the repo root): NODRIVE_SCRATCH=<dir> spikes/remote/mac-sync-src.sh
set -e
SP="${NODRIVE_SCRATCH:-/tmp}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
COPYFILE_DISABLE=1 tar czf "$SP/nodrive-src.tgz" -C "$ROOT" --exclude='*/bin' --exclude='*/obj' src eng Directory.Build.props Directory.Packages.props global.json NoDrive.slnx .editorconfig
"$HERE/mac-put.sh" "$SP/nodrive-src.tgz" "C:/Users/<PC_USER>/nodrive-src.tgz"
"$HERE/mac-pc.sh" 'cd C:\Projects\Personal\nodrive; Get-ChildItem -Recurse -Force -Filter "._*" | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue; tar xzf $env:USERPROFILE\nodrive-src.tgz; dotnet publish src/NoDrive.Engine -c Release -r win-x64 -p:PublishAot=false --self-contained false -o $env:USERPROFILE\nodrive-dev\engine 2>&1 | Select-String " error |->" | Select-Object -Last 3'
