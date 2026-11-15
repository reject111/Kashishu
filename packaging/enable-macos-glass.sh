#!/usr/bin/env bash
set -euo pipefail

apphost="$1"
# AppKit selects its control design using the MAIN executable's linked SDK, not
# libAvaloniaNative's SDK. Opt the .NET launcher into macOS 26's native appearance
# without raising its deployment target, so macOS 12–15 retain their native design.
build_info="$(xcrun vtool -show-build "$apphost")"
linked_sdk="$(printf '%s\n' "$build_info" | awk '/^[[:space:]]*sdk / { print $2; exit }')"
# Intel hosts may still encode their deployment target in LC_VERSION_MIN_MACOSX.
minimum_os="$(printf '%s\n' "$build_info" | awk '/^[[:space:]]*(minos|version) / { print $2; exit }')"
if [[ -z "$linked_sdk" || -z "$minimum_os" ]]; then
  printf 'Unable to read the macOS apphost build version: %s\n' "$apphost" >&2
  exit 1
fi
if (( ${linked_sdk%%.*} >= 26 )); then
  exit 0
fi

xcrun vtool -set-build-version macos "$minimum_os" 26.0 -replace \
  -output "$apphost.glass-tmp" "$apphost"
mv -f -- "$apphost.glass-tmp" "$apphost"
chmod +x "$apphost"
# Editing a Mach-O load command invalidates its signature. Build outputs use an
# ad-hoc signature; build-macos.sh signs the finished application bundle again.
codesign --force --sign - "$apphost"
