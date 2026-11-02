# Ampelum.ComparativeBenchmarks

Cross-framework BenchmarkDotNet comparison: **Ampelum vs MEAI raw vs
Semantic Kernel vs AutoGen.NET vs LangChain.NET vs DSPy-py (subprocess)**.

This project is **not** part of the unit-test suite, **not** coverage-gated,
and **not** wired into any per-PR CI check. It runs nightly (alpha.6) and
on demand from a developer machine to track the §B3 must-be-#1 claims for
the v1.0 release.

## What ships in v0.9.1-alpha.5 (this milestone)

- **6 new scenarios**: S4 streaming throughput, S5 tool-calling round-trip
  (dispatch-isolation cut), S6 audit chain throughput, S7 AOT cold-start
  (driver-script harness — see `tools/measure-cold-start.sh`),
  S8 optimizer compile time (Ampelum vs Python reference), S9 N-parallel
  scaling at N=1/8/32/64.
- **Python subprocess harness body** — `PythonDspyRunner.RunAsync` is wired
  via `Process.Start`, JSON-line wire protocol, with availability probing
  for `uv`/`python`/`python3` on PATH. BDN has no `[Skip]`; scenarios
  short-circuit to a sentinel result when Python is absent.
- **`python-ref/` scaffolding** — `pyproject.toml` (uv-managed) +
  `requirements.txt` (pip fallback) + `runner.py` implementing the S8
  scenario. README documents `uv sync` setup and CI authority.

## What shipped in v0.9.1-alpha.4

- Project skeleton with isolated CPM (`Directory.Packages.props` shadows the
  root pin set so SK/AutoGen/LangChain transitive closures can never collide
  with the SUT).
- **3 scenarios**, picked by must-be-#1 priority from the v1-decisions §B3
  list:
  - `Scenario01_SinglePredict` — user-brief **S1**. Single Predict no-hooks
    orchestration overhead. Budget D1: ≤ MEAI raw + 500 ns and ≤ +256 B.
  - `Scenario02_PredictPlus4HookPipeline` — user-brief **S3**. Predict + 4
    noop IHook impls (alpha.5 swaps in real budget+audit+retry+log). Budget
    D2: 4-hook overhead ≤ 10 µs.
  - `Scenario12_AllocationPerPredict` — user-brief **S2**. Allocation per
    Forward via `[MemoryDiagnoser]`. Budget D3: ≤ 512 B no-hooks.
- Competitor placeholders for SK / AutoGen.NET / LangChain.NET that route
  through the same `IChatClient` mock so LM cost is held constant and the
  delta is pure orchestration. alpha.5 swaps these for the idiomatic
  Kernel.InvokeAsync / agent-harness / chain.RunAsync paths.
- `PythonDspyRunner` skeleton — contract surface only. alpha.5 wires the
  real `Process.Start` body + uv-managed venv + JSON wire protocol.

## User-brief → plan-file mapping

The user's alpha.4 brief labels the three scenarios `S1/S2/S3`. The on-disk
filenames follow the v0.9h plan numbering so alpha.5/alpha.6 can plug in
S3-S15 without renumbering:

| User brief | Plan file                                  | Budget | Tier |
|------------|--------------------------------------------|--------|------|
| **S1**     | `Scenario01_SinglePredict.cs`              | D1     | #1   |
| **S2**     | `Scenario12_AllocationPerPredict.cs`       | D3     | #1   |
| **S3**     | `Scenario02_PredictPlus4HookPipeline.cs`   | D2     | #1   |

## How to run

From the repo root:

```bash
dotnet build tests/Ampelum.ComparativeBenchmarks -c Release
dotnet run --project tests/Ampelum.ComparativeBenchmarks -c Release -- \
  --filter '*Scenario01*'
```

Run all three current scenarios:

```bash
dotnet run --project tests/Ampelum.ComparativeBenchmarks -c Release -- \
  --filter '*Scenario01*|*Scenario02*|*Scenario12*'
```

The **must** keep the `Release` flag — Debug-mode benchmarks are
meaningless.

## How to interpret the output

BenchmarkDotNet writes two artefacts:

- A console table with `Mean | Error | StdDev | Allocated | Ratio`. The
  `Ratio` column compares each row to the `[Benchmark(Baseline = true)]`
  Ampelum row, so `Ratio < 1.00` means Ampelum is slower (regression).
- Markdown + JSON exports to `BenchmarkDotNet.Artifacts/results/`. The
  `*-report-full-compressed.json` files are what `tools/comparative-rank.csx`
  (alpha.5) consumes to compute 95% confidence intervals and emit the
  must-be-#1 verdict per scenario.

For the alpha.4 must-be-#1 budget checks:

- **Scenario01** (D1) — Ampelum `Mean` must not exceed `MeaiRaw.Mean + 500 ns`,
  and Ampelum `Allocated` must not exceed `MeaiRaw.Allocated + 256 B`.
- **Scenario02** (D2) — Ampelum `Mean` minus the no-hooks Scenario01 Ampelum
  `Mean` must be ≤ 10 µs.
- **Scenario12** (D3) — Ampelum `Allocated` must be ≤ 512 B.

CI enforcement of these gates lands in alpha.5 along with
`tools/comparative-rank.csx`.

## Python reference setup (deferred to alpha.5)

`PythonDspyRunner` (in `SubprocessHarness/`) is a skeleton; calling
`RunAsync` throws `NotImplementedException`. alpha.5 wires:

1. `tests/Ampelum.ComparativeBenchmarks/python-ref/pyproject.toml`
   (`python_requires = ">=3.12"`) + `uv.lock` for reproducibility.
2. `tests/Ampelum.ComparativeBenchmarks/python-ref/requirements.txt`
   listing the Python reference packages.
3. The `Process.Start` body that pipes a one-line JSON request in and
   reads a one-line JSON response back.

Until alpha.5 ships, the `Sk` / `AutoGenNet` / `LangChainNet` benchmark
rows give a 4-competitor comparison without any Python dependency.

## Why the project is isolated

The sibling `Directory.Packages.props` shadows the root pin set. MSBuild
walks **up** from the `.csproj` and stops at the first
`Directory.Packages.props` it finds, so the competitor pins (SK 1.74.0,
AutoGen 0.2.3, LangChain 0.17.0) and any version drift they bring along
cannot perturb the root package graph. The unit-test suite stays on the
exact MEAI 10.5.x and OpenAI 2.10.x our production library targets.

The csproj-scoped `<NoWarn>` covers competitor-specific warnings only and
is documented inline. `TreatWarningsAsErrors=true` from the root
`Directory.Build.props` is preserved.

## Deferred to alpha.6 (final)

- Scenarios **S10/S11/S13/S14/S15** (5 remaining: RAG retrieval round-trip,
  MIPROv2 compile, streaming-cancel propagation, save/load, dataset
  materialisation).
- `tools/comparative-rank.csx` — 95% CI + must-be-#1 ranker reading
  `BenchmarkDotNet.Artifacts/results/*-report-full-compressed.json` and
  emitting per-scenario verdicts.
- `.github/workflows/comparative-nightly.yml` (full comparative + Pareto
  real-LM) and `.github/workflows/pareto-integration.yml` (per-PR mock-LM).
- Idiomatic SK `Kernel.InvokeAsync` + AutoGen agent harness + LangChain
  chain wiring so orchestration deltas reflect each framework's typical
  usage rather than a placeholder direct chat call.
- `S5` full LM-driven tool round-trip (the alpha.5 cut benches dispatch
  in isolation; alpha.6 wires a two-call mock and SK
  `AutoInvokeKernelFunctions`).
- `S6` `BatchedAuditSink` throughput comparison — the alpha.5 cut benches
  the per-entry path against a naive hand-rolled chain; the must-be-#1
  story rests on batched amortisation, which lands once
  `tools/comparative-rank.csx` can read multi-row throughput summaries.
