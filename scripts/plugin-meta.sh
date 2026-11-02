#!/usr/bin/env bash
# Writes the plugin's meta.json from the project file and prints its path.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

project="src/Jellyfin.Plugin.ExperienceManager"
dotnet msbuild "$project" -nologo -v:q -p:Configuration=Release -t:WritePluginMeta >&2
dir="$(dotnet msbuild "$project" -nologo -p:Configuration=Release -getProperty:IntermediateOutputPath | tr -d '\r')"
echo "$project/${dir//\\//}meta.json"
