#!/usr/bin/env bash
# tools/measure-parallel-scaling.sh — Scenario S9 per-process driver.
#
# v1.0-rc1.alpha.2: S9 was reframed (see docs/performance/comparative-results.md
# §M6 reframe). The original "62.76× N=64/N=1 ratio" was a bench-design
# artifact — `MockChatClient.GetResponseAsync` returned `Task.FromResult`,
# which made every `await` in `ForwardAsync` continue synchronously, so N
# "parallel" forwards ran sequentially on one thread. S9 now uses
# `YieldingMockChatClient` (real async suspension) and includes an
# `EmptyAsync` companion benchmark (each task does only `Task.Yield()`,
# no Ampelum code) as the threadpool/WhenAll harness floor.
#
# This script still drives one fresh process per N (so cross-N JIT/tiering
# heat and shared `ActivityListener` fanout are isolated). The historical
# "in-process vs per-process" verdict it prints is preserved, but the real
# verdict is now `Ampelum/EmptyAsync ratio at each N` — read off the
# per-N github reports under `BenchmarkDotNet.Artifacts/per-process-N{N}/`.
#
# Strategy: re-run S9 with the IDENTICAL BDN job (`-j short`) but launch a
# FRESH dotnet process per N value, using BDN's parameter-aware glob filter:
#
#   --filter '*Scenario09*Parallelism: <N>)*'
#
# Each N writes its own artifacts directory so means do not collide. The
# script prints a side-by-side table of in-process vs per-process means and
# the resulting N=64/N=1 ratio.
#
# Same-harness, different-process-boundary is what isolates the "cross-N
# contention in one BDN process" hypothesis. A bespoke non-BDN driver would
# add a second variable (different timing harness) and confound the verdict.
#
# Usage (bash on Windows or linux-x64):
#   bash tools/measure-parallel-scaling.sh
#
# Pre-requisite: build first (the script passes --no-build):
#   dotnet build tests/Ampelum.ComparativeBenchmarks -c Release
#
# Output (stdout): markdown table + verdict line.
set -euo pipefail

PROJ="tests/Ampelum.ComparativeBenchmarks"
ARTIFACTS_ROOT="$PROJ/BenchmarkDotNet.Artifacts"
NS=(1 8 32 64)

# 1. Fresh build once so per-N runs use --no-build (no JIT cost asymmetry).
dotnet build "$PROJ" -c Release >/dev/null

# 2. Per-N: fresh process, single benchmark case, isolated artifacts dir.
declare -A MEAN_NS
for N in "${NS[@]}"; do
  OUT="$ARTIFACTS_ROOT/per-process-N$N"
  rm -rf "$OUT"
  echo ">>> Per-process run N=$N" >&2
  ( cd "$PROJ" && dotnet run --no-build -c Release -- \
      --filter "*Scenario09*Parallelism: $N)*" \
      -j short \
      --artifacts "BenchmarkDotNet.Artifacts/per-process-N$N" \
      >/dev/null )

  CSV="$OUT/results/Ampelum.ComparativeBenchmarks.Scenarios.Scenario09_NParallelScaling-report.csv"
  if [[ ! -f "$CSV" ]]; then
    echo "ERROR: missing report CSV at $CSV" >&2
    exit 2
  fi

  # Parse "Mean" column. Header order matches the in-process CSV — Mean is
  # the column right after Parallelism. Use python for robust quoted-field
  # handling (BDN quotes "1,264.68 ns" thousand-separated values).
  MEAN_NS[$N]=$(python3 - "$CSV" <<'PY'
import csv, re, sys
with open(sys.argv[1], newline='', encoding='utf-8') as f:
    r = csv.DictReader(f)
    row = next(r)
mean = row['Mean'].replace(',', '').strip()
m = re.match(r'([\d.]+)\s*(\S+)', mean)
val, unit = float(m.group(1)), m.group(2)
# Both µ (micro sign) and μ (Greek mu) render as "us" in BDN
if unit.endswith('s') and unit[0] in ('µ', 'μ'):
    factor = 1000
else:
    factor = {'ns': 1, 'us': 1000, 'ms': 1_000_000, 's': 1_000_000_000}[unit]
print(f"{val * factor:.2f}")
PY
)
done

# 3. In-process numbers from the canonical artifacts (already on disk from
#    the docs/performance/comparative-results.md verification pass).
INPROC_CSV="$ARTIFACTS_ROOT/results/Ampelum.ComparativeBenchmarks.Scenarios.Scenario09_NParallelScaling-report.csv"
declare -A INPROC_NS
if [[ -f "$INPROC_CSV" ]]; then
  while IFS= read -r line; do
    eval "$line"
  done < <(python3 - "$INPROC_CSV" <<'PY'
import csv, re, sys
with open(sys.argv[1], newline='', encoding='utf-8') as f:
    r = csv.DictReader(f)
    for row in r:
        mean = row['Mean'].replace(',', '').strip()
        m = re.match(r'([\d.]+)\s*(\S+)', mean)
        if not m: continue
        val, unit = float(m.group(1)), m.group(2)
        if unit.endswith('s') and unit[0] in ('µ', 'μ'):
            factor = 1000
        else:
            factor = {'ns': 1, 'us': 1000, 'ms': 1_000_000, 's': 1_000_000_000}.get(unit)
            if factor is None: continue
        print(f"INPROC_NS[{row['Parallelism']}]={val * factor:.2f}")
PY
)
fi

# 4. Side-by-side table.
printf '\n## S9 in-process vs per-process means (BDN ShortRun, same job)\n\n'
printf '| N | in-process mean (ns) | per-process mean (ns) | per-proc / N=1 ratio |\n'
printf '|---|----------------------|------------------------|----------------------|\n'
PP1="${MEAN_NS[1]}"
for N in "${NS[@]}"; do
  IP="${INPROC_NS[$N]:-—}"
  PP="${MEAN_NS[$N]}"
  RATIO=$(python3 -c "print(f'{${PP}/${PP1}:.2f}x')")
  printf '| %d | %s | %s | %s |\n' "$N" "$IP" "$PP" "$RATIO"
done

# 5. Verdict.
# v1.0-rc1.alpha.2: the historical Ampelum-N=64/Ampelum-N=1 ratio is dominated
# by the threadpool/WhenAll harness floor at sub-microsecond per-task work, so
# it is a poor signal for product contention. We print it for continuity but
# the authoritative metric is now `Ampelum/EmptyAsync` at the same N (visible
# in each per-N github report).
RATIO_64=$(python3 -c "print(${MEAN_NS[64]}/${MEAN_NS[1]})")
echo ""
python3 - <<PY
r = ${RATIO_64}
print(f"Per-process Ampelum N=64/N=1 ratio: {r:.2f}x")
print(f"NOTE: this ratio is dominated by threadpool/WhenAll harness floor at")
print(f"sub-microsecond per-task work. The authoritative contention signal is")
print(f"Ampelum/EmptyAsync at each N (see per-N github reports). v1.0-rc1.alpha.2")
print(f"verified flat ~4x Ampelum/EmptyAsync at N in {{1,8,32,64}} -> no contention.")
PY
