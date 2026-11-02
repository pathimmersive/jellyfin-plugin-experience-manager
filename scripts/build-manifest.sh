#!/usr/bin/env bash
# Writes the Jellyfin plugin repository manifest.json.
#
# Usage: scripts/build-manifest.sh <manifest.json> --entries <dir>
#   --entries <dir>  read the entries scripts/package.sh wrote into <dir>
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <manifest.json> --entries <dir>" >&2
  exit 2
fi

out="$1"
shift
# The plugin's name, description and guid come from the meta.json the project writes.
meta="$(bash scripts/plugin-meta.sh)"
dotnet run scripts/release-tool.cs -- manifest --meta "$meta" --out "$out" "$@"
