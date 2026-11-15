#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$(mktemp -d)"
trap 'rm -rf -- "$publish_dir"' EXIT

case "$(uname -m)" in
  arm64) runtime=osx-arm64 ;;
  x86_64) runtime=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

git -C "$project_dir" submodule update --init --recursive
command -v dotnet >/dev/null || { echo 'dotnet SDK is required' >&2; exit 1; }

dotnet publish "$project_dir/LyricifyIsland.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  --output "$publish_dir"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project_dir/LyricifyIsland.csproj" | head -1)"
[[ -n "$version" ]] || { echo 'Version missing from LyricifyIsland.csproj' >&2; exit 1; }

app="$project_dir/dist/LyricifyIsland.app"
rm -rf -- "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$app/Contents/Helpers"
install -m755 "$publish_dir/LyricifyIsland" "$app/Contents/MacOS/LyricifyIsland"
[[ -x "$publish_dir/LyricifyIslandGlass" ]] || {
  echo 'Native Liquid Glass island helper was not built' >&2
  exit 1
}
install -m755 "$publish_dir/LyricifyIslandGlass" "$app/Contents/Helpers/LyricifyIslandGlass"

# The bundle is an accessory app (LSUIElement): it lives in the menu bar and does not add a
# Dock icon, matching the tray-only behaviour of the Linux and Windows builds.
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh_CN</string>
    <key>CFBundleDisplayName</key>
    <string>Lyricify Island</string>
    <key>CFBundleExecutable</key>
    <string>LyricifyIsland</string>
    <key>CFBundleIdentifier</key>
    <string>com.lyricifyisland.app</string>
    <key>CFBundleIconFile</key>
    <string>LyricifyIsland</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Lyricify Island</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSAppleEventsUsageDescription</key>
    <string>读取 Spotify 和 Music 的歌曲与播放进度，并响应播放、切歌和收藏操作。</string>
</dict>
</plist>
PLIST

# Build the .icns from the icon embedded in the app; iconutil needs the full iconset.
icon="$project_dir/vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper/Resources/icon.png"
if [[ -f "$icon" ]] && command -v sips >/dev/null && command -v iconutil >/dev/null; then
  iconset="$publish_dir/LyricifyIsland.iconset"
  mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/LyricifyIsland.icns" >/dev/null
fi

license_dir="$app/Contents/Resources/licenses"
mkdir -p "$license_dir" "$project_dir/dist/licenses"
install -m644 "$project_dir/THIRD_PARTY_NOTICES.md" "$app/Contents/Resources/THIRD_PARTY_NOTICES.md"
install -m644 "$project_dir/LICENSE" "$license_dir/LyricifyIsland-Apache-2.0.txt"
install -m644 "$project_dir/licenses/OppoPodsManager-GPL-3.0.txt" "$license_dir/OppoPodsManager-GPL-3.0.txt"
install -m644 "$project_dir/vendor/Lyricify-Lyrics-Helper/LICENSE" "$license_dir/Lyricify-Lyrics-Helper-Apache-2.0.txt"
install -m644 "$app/Contents/Resources/THIRD_PARTY_NOTICES.md" "$project_dir/dist/THIRD_PARTY_NOTICES.md"
ditto "$license_dir" "$project_dir/dist/licenses"

# Ad-hoc signing keeps the app's identity stable enough for the Automation permission prompt.
if command -v codesign >/dev/null; then
  codesign --force --sign - "$app/Contents/Helpers/LyricifyIslandGlass" >/dev/null
  codesign --force --deep --sign - "$app" >/dev/null
  codesign --verify --deep --strict "$app"
fi

ln -sfn "LyricifyIsland.app/Contents/MacOS/LyricifyIsland" "$project_dir/dist/LyricifyIsland"

# The DMG is unsigned/not notarized like the app bundle; it exists so the CI release and
# local builds hand out the same drag-and-drop installer. The Applications link lets the
# user install by dropping the app onto it.
if command -v hdiutil >/dev/null; then
  stage="$publish_dir/dmg"
  mkdir -p "$stage"
  ditto "$app" "$stage/LyricifyIsland.app"
  ln -sfn /Applications "$stage/Applications"
  dmg="$project_dir/dist/LyricifyIsland-${version}-$(uname -m).dmg"
  rm -f -- "$dmg"
  hdiutil create -quiet -volname "Lyricify Island" -srcfolder "$stage" -ov -format UDZO "$dmg"
  printf 'Built %s\n' "$dmg"
fi
printf 'Built %s\n' "$app"
