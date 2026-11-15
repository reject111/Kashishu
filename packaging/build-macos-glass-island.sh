#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:?output directory is required}"
runtime_identifier="${2:-}"
configuration="${3:-Debug}"
source_file="$project_dir/native/macos/main.swift"
output_file="$output_dir/LyricifyIslandGlass"
module_cache="${TMPDIR:-/tmp}/lyricify-island-swift-module-cache"

case "$runtime_identifier" in
  osx-arm64) swift_arch=arm64 ;;
  osx-x64) swift_arch=x86_64 ;;
  "")
    case "$(uname -m)" in
      arm64) swift_arch=arm64 ;;
      x86_64) swift_arch=x86_64 ;;
      *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
    esac
    ;;
  *) echo "Unsupported macOS runtime identifier: $runtime_identifier" >&2; exit 1 ;;
esac

command -v xcrun >/dev/null || {
  echo 'Xcode Command Line Tools are required to build the native Liquid Glass island' >&2
  exit 1
}

sdk="$(xcrun --show-sdk-path)"
sdk_version="$(xcrun --show-sdk-version)"
if (( ${sdk_version%%.*} < 26 )); then
  echo 'The macOS 26 SDK or newer is required to build the native Liquid Glass island' >&2
  exit 1
fi
mkdir -p "$output_dir" "$module_cache"
export CLANG_MODULE_CACHE_PATH="$module_cache"
export SWIFT_MODULE_CACHE_PATH="$module_cache"
swift_flags=(-D RELEASE)
if [[ "$configuration" == Debug ]]; then
  swift_flags=(-D DEBUG -framework ScreenCaptureKit)
fi
xcrun swiftc \
  "${swift_flags[@]}" \
  -O \
  -whole-module-optimization \
  -parse-as-library \
  -target "$swift_arch-apple-macosx26.0" \
  -sdk "$sdk" \
  -framework AppKit \
  -framework QuartzCore \
  -framework CoreImage \
  "$project_dir/native/macos/IslandMotion.swift" \
  "$project_dir/native/macos/IslandInteraction.swift" \
  "$project_dir/native/macos/LyricRenderer.swift" \
  "$source_file" \
  -o "$output_file"
chmod 755 "$output_file"
