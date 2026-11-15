#!/usr/bin/env bash
set -euo pipefail

macos_version="$(sw_vers -productVersion)"
if (( ${macos_version%%.*} < 26 )); then
  echo 'The native island integration test requires macOS 26 or newer' >&2
  exit 1
fi

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
app="${1:-$project_dir/dist/LyricifyIsland.app}"
output_dir="${2:-$(mktemp -d "${TMPDIR:-/tmp}/lyricify-macos-integration.XXXXXX")}"
mkdir -p "$output_dir"
python3 - "$app" "$output_dir" <<'PYTHON'
import json
import os
from pathlib import Path
import subprocess
import sys

app = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
executable = app / "Contents/MacOS/LyricifyIsland"
assert executable.is_file(), "Build LyricifyIsland.app first"
config = output / "config/lyricify-island"
config.mkdir(parents=True, exist_ok=True)
def reset_settings():
    (config / "settings.json").write_text(json.dumps({"AcrylicIsland": True, "EnableTrackOffsets": True}))

environment = dict(os.environ, XDG_CONFIG_HOME=str(config.parent),
                   XDG_STATE_HOME=str(output / "state"), XDG_CACHE_HOME=str(output / "cache"))

scenarios = {
    "mismatch": ('print(\'{"type":"ready","protocolVersion":999}\', flush=True)\nimport time; time.sleep(20)',
                 "protocol mismatch", 4),
    "exit": ('print(\'{"type":"ready","protocolVersion":1}\', flush=True)\nimport time; time.sleep(0.4)',
             "restoring the Avalonia island", 4),
    "blocked": ('''import json, time
print('{"type":"ready","protocolVersion":1}', flush=True)
for position in range(2048):
    print(json.dumps({"type":"action", "action":"position", "x":position, "y":0}), flush=True)
time.sleep(20)
''',
                "restoring the Avalonia island", 12),
    "actions": ('''import json, sys
print('{"type":"ready","protocolVersion":1}', flush=True)
sent = False
for line in sys.stdin:
    message = json.loads(line)
    if message["type"] == "quit": break
    if message["type"] != "playback" or sent: continue
    track_id = message["playback"].get("trackId")
    if not track_id: continue
    sent = True
    for action in [
        {"action":"position", "x":1e30,"y":0},
        {"action":"offset", "trackId":track_id,"delta":"invalid"},
        {"action":"position"},
        {"action":"playPause", "trackId":"stale-track"},
        {"action":"lyricsWindow"},
        {"action":"settings"},
        {"action":"banner", "trackId":track_id},
        {"action":"playPause", "trackId":track_id}
    ]:
        print(json.dumps(dict(type="action", **action)), flush=True)
    print("integration actions sent", file=sys.stderr, flush=True)
''', "integration actions sent", 5)
}

for name, (body, expected, duration) in scenarios.items():
    reset_settings()
    helper = output / (name + "-helper")
    helper.write_text("#!" + sys.executable + "\n" + body + "\n")
    helper.chmod(0o755)
    result = subprocess.run([str(executable), "--demo", "--exit-after", str(duration)],
                            env=dict(environment, LYRICIFY_MACOS_GLASS_HELPER=str(helper)),
                            capture_output=True, text=True, timeout=duration + 10)
    (output / (name + ".log")).write_text(result.stdout + result.stderr)
    assert result.returncode == 0, (name, result.returncode, result.stderr)
    assert expected in result.stderr, (name, "expected event missing", result.stderr)
    if name == "actions":
        assert "invalid action:" in result.stderr, "Malformed actions must be rejected safely"
    print(name + ": ok")

reset_settings()
result = subprocess.run([str(executable), "--demo", "--exit-after", "4"],
                        env=environment, capture_output=True, text=True, timeout=14)
(output / "native.log").write_text(result.stdout + result.stderr)
assert result.returncode == 0 and "[macos-glass] started" in result.stderr, result.stderr
assert "restoring the Avalonia island" not in result.stderr, result.stderr
print("native helper lifecycle: ok")
print("macOS integration output: " + str(output))
PYTHON
