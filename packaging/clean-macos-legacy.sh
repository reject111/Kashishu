#!/usr/bin/env bash
set -euo pipefail

# Only generated artifacts in this checkout. Keep Windows RID outputs and user data.
project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
for directory in dist bin obj; do
  root="$project_dir/$directory"
  [[ -d "$root" && ! -L "$root" ]] || continue
  while IFS= read -r -d '' artifact; do
    rm -rf -- "$artifact"
  done < <(find "$root" \( -type d \( -name 'win-*' -o -name 'net*-windows*' \) -prune \) -o \
    \( -name 'LyricifyIsland.app' -o -name 'LyricifyIsland' \
    -o -name 'LyricifyIslandGlass' -o -name 'LyricifyIsland-*.dmg' \
    -o -name 'LyricifyIsland.dll' -o -name 'LyricifyIsland.pdb' \
    -o -name 'LyricifyIsland.deps.json' -o -name 'LyricifyIsland.runtimeconfig.json' \
    -o -name 'LyricifyIsland.csproj.*' -o -name 'LyricifyIsland.*.cache' -o -name 'LyricifyIsland.AssemblyInfo*' \
    -o -name 'LyricifyIsland.GeneratedMSBuildEditorConfig.editorconfig' \
    -o -name 'LyricifyIsland.GlobalUsings.g.cs' -o -name 'LyricifyIsland.sourcelink.json' \
    -o -name 'LyricifyIsland-Apache-2.0.txt' \) -prune -print0)
done
