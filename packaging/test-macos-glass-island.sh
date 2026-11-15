#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$(mktemp -d "${TMPDIR:-/tmp}/lyricify-glass-tests.XXXXXX")}"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
module_cache="${TMPDIR:-/tmp}/lyricify-island-swift-module-cache"
mkdir -p "$module_cache"
export CLANG_MODULE_CACHE_PATH="$module_cache"
export SWIFT_MODULE_CACHE_PATH="$module_cache"
xcrun swiftc -parse-as-library \
  "$project_dir/native/macos/IslandMotion.swift" \
  "$project_dir/native/macos/IslandInteraction.swift" \
  "$project_dir/native/macos/Tests/IslandMotionTests.swift" \
  -o "$output_dir/IslandMotionTests"
"$output_dir/IslandMotionTests"

if [[ "${2:-}" == --visual ]]; then
  app="$output_dir/LyricifyGlassVisualTest.app"
  mkdir -p "$app/Contents/MacOS"
  bash "$project_dir/packaging/build-macos-glass-island.sh" "$app/Contents/MacOS" "" Debug
  install -m644 "$project_dir/native/macos/Tests/VisualTest-Info.plist" "$app/Contents/Info.plist"
  codesign --force --sign - "$app"
  for appearance in dark light reduced; do
    captures="$output_dir/$appearance"
    mkdir -p "$captures"
    reduced_motion=0
    [[ "$appearance" != reduced ]] || reduced_motion=1
    /usr/bin/open -n -W \
      --stdout "$captures/stdout.log" --stderr "$captures/stderr.log" \
      --env LYRICIFY_GLASS_VISUAL_TEST=1 \
      --env "LYRICIFY_GLASS_CAPTURE_DIR=$captures" \
      --env "LYRICIFY_GLASS_APPEARANCE=$appearance" \
      --env "LYRICIFY_GLASS_REDUCE_MOTION=$reduced_motion" \
      "$app"
  done
  xcrun swiftc -parse-as-library \
    "$project_dir/native/macos/Tests/VisualRegression.swift" \
    -o "$output_dir/VisualRegression"
  "$output_dir/VisualRegression" "$output_dir"
fi
printf 'Native glass test output: %s\n' "$output_dir"
