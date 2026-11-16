#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ -x "$project_dir/dist/Kashishu" ]] || "$project_dir/build-macos.sh"

cd "$project_dir"
if (( $# > 0 )); then
  exec "$project_dir/dist/Kashishu" "$@"
fi
exec /usr/bin/open -n "$project_dir/dist/Kashishu.app"
