#!/bin/bash
# tools/shot.sh out.png [game args...]   e.g. tools/shot.sh /tmp/a.png --seed=3 --auto --time=40 --shot-view=melee
# Builds, runs the game windowed, saves a screenshot after a few frames and quits.
# On macOS the game is launched through LaunchServices (`open`), which also works from shells that aren't
# attached to the GUI session (where a direct exec of the binary hangs). SHOT_DIRECT=1 forces a direct exec.
GODOT=${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
out=$1; shift
[ -z "$out" ] && { echo "usage: tools/shot.sh out.png [game args...]"; exit 2; }
case "$out" in /*) ;; *) out="$PWD/$out" ;; esac   # Godot runs from the project dir
(cd "$ROOT" && dotnet build -nologo -v q >/dev/null) || { echo "build failed"; exit 1; }
filter() { grep -E "SCRIPT ERROR|ERROR|Exception|\[shot\]|at: " | head -20; }
if [ "$(uname)" = Darwin ] && [ -z "$SHOT_DIRECT" ]; then
    APP="${GODOT%/Contents/MacOS/*}"
    log=$(mktemp -t shot)
    perl -e 'alarm 120; exec @ARGV' open -W -n --stdout "$log" --stderr "$log" -a "$APP" \
        --args --path "$ROOT" res://scenes/main.tscn -- --screenshot="$out" "$@"
    filter < "$log"
    rm -f "$log"
else
    perl -e 'alarm 120; exec @ARGV' "$GODOT" --path "$ROOT" res://scenes/main.tscn -- --screenshot="$out" "$@" 2>&1 | filter
fi
[ -f "$out" ]
