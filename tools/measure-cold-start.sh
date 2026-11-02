#!/usr/bin/env bash
# tools/measure-cold-start.sh — Scenario S7 driver.
#
# Measures Ampelum's AOT-published cold-start: time from process spawn to
# first Predict.ForwardAsync completion. BenchmarkDotNet cannot measure
# cold-start (its warm-up loop defeats the measurement), so v0.9.1-alpha.5
# ships an outer driver script that times the AOT smoke binary directly.
#
# Authoritative platform: linux-x64 (matches Ampelum.AotSmoke CI gate).
# On Windows, AOT publish may fail under Smart App Control;
# this script will still run if the binary is present but the cold-start
# numbers are not the comparative-bench gate.
#
# Usage:
#   tools/measure-cold-start.sh [iterations]   # default 10
#
# Output (stdout): one JSON line:
#   {"iterations": N, "min_ms": X, "p50_ms": X, "p95_ms": X, "max_ms": X}
#
# Budget D4: ≤30 ms p50 on linux-x64.
#
# Pre-requisite: publish the AOT smoke binary first:
#   dotnet publish tests/Ampelum.AotSmoke -c Release -r linux-x64
set -euo pipefail

ITER="${1:-10}"

BIN_LINUX="tests/Ampelum.AotSmoke/bin/Release/net10.0/linux-x64/publish/Ampelum.AotSmoke"
BIN_WIN="tests/Ampelum.AotSmoke/bin/Release/net10.0/win-x64/publish/Ampelum.AotSmoke.exe"
if [[ -x "$BIN_LINUX" ]]; then
  BIN="$BIN_LINUX"
elif [[ -x "$BIN_WIN" ]]; then
  BIN="$BIN_WIN"
else
  echo "AOT binary not found. Run:" >&2
  echo "  dotnet publish tests/Ampelum.AotSmoke -c Release -r linux-x64" >&2
  exit 2
fi

# Per-iteration: spawn, time wall-clock, parse exit code.
# /usr/bin/time -f is GNU-only; we use bash builtins for portability.
TMPF="$(mktemp)"
trap 'rm -f "$TMPF"' EXIT

for i in $(seq 1 "$ITER"); do
  T0=$(date +%s%N)
  "$BIN" >/dev/null 2>&1
  T1=$(date +%s%N)
  echo "$(( (T1 - T0) / 1000000 ))" >>"$TMPF"
done

# Sort and pluck min/p50/p95/max in pure awk.
sort -n "$TMPF" | awk -v n="$ITER" '
  { a[NR]=$1 }
  END {
    p50_idx = int((n+1)*0.5); if (p50_idx<1) p50_idx=1; if (p50_idx>n) p50_idx=n
    p95_idx = int((n+1)*0.95); if (p95_idx<1) p95_idx=1; if (p95_idx>n) p95_idx=n
    printf "{\"iterations\":%d,\"min_ms\":%d,\"p50_ms\":%d,\"p95_ms\":%d,\"max_ms\":%d}\n",
      n, a[1], a[p50_idx], a[p95_idx], a[n]
  }
'
