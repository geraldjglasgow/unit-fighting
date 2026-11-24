#!/bin/bash
# Screenshot the battle visuals without the game layer (res://tools/view_preview.tscn).
#   tools/view_shot.sh out.png [--time=40] [--shot-view=overview|army|melee|close|duel|line] [--shot-frames=20] [--speed=1]
#                              [--seed=7] [--scenario=default|duel|spears|chosen|big|charge|line] [--men=2] [--gap=5] [--copies=4]
#                              [--holder=legionaries --attacker=warband]  (line: who holds, who walks into them)
#                              [--charger=warband --holder=legionaries --formation=line]  (charge: who runs at whom)
#                              [--burst=K --burst-every=N]  (K shots N frames apart: out_0.png ...)
#                              [--find=death|down|impact|press|shove|feint [--lead=0.3]]  (replay to just before the next death /
#                                     knockdown / charge impact / two shieldmen chest to chest / shield shove / feint bitten on)
#                              [--scenario=gallery --blows=Cut,Overhead,feint:Cut,probe:Thrust,Bash --outcome=block|parry|hit|miss]
#                                     (no sim: one man throws the listed blows in turn at another; --blows=none for the guard)
#                              [--yaw= --pitch= --dist= --target=x,z] [--follow] [--track] [--subject=ID]
#                              [--select=I] [--hover-unit=I] [--hover-man] [--inspect] [--preview]
#                              [--perf] [--motionstats] [--tire=0.2] [--camtest] [--camlog] [--simstats] [--fog=0]
# Run `dotnet build` first. A window opens briefly.
# If a direct launch hangs (a shell outside the macOS GUI session), set SHOT_OPEN=1 to launch through `open -W`.
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?usage: view_shot.sh out.png [args]}"
shift
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac
mkdir -p "$(dirname "$OUT")"
GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
if [ -n "$SHOT_OPEN" ] && [ "$(uname)" = Darwin ]; then
    APP="${GODOT%/Contents/MacOS/*}"
    log=$(mktemp -t viewshot)
    perl -e 'alarm 120; exec @ARGV' open -W -n --stdout "$log" --stderr "$log" -a "$APP" \
        --args --path "$ROOT" res://tools/view_preview.tscn -- --screenshot="$OUT" "$@"
    cat "$log"
    rm -f "$log"
else
    exec perl -e 'alarm 120; exec @ARGV' "$GODOT" --path "$ROOT" res://tools/view_preview.tscn -- --screenshot="$OUT" "$@"
fi
