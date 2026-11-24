#!/usr/bin/env bash
# Headless combat balance bench. Usage: tools/simbench.sh [all|duel|duel-charge|1v2|lines|flank|matchups|default|perf] [--seeds N]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec dotnet run -c Release --project "$root/tests/SimBench/SimBench.csproj" -- "$@"
