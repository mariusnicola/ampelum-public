# Python reference runner

Subprocess target invoked by `PythonDspyRunner` (C# side) to bench the
Python reference framework against Ampelum on selected scenarios.

This folder is the **only** place upstream framework names live in the
repo. Per the v1.0 rebrand spec the alpha.6 grep gate allowlists
`tests/Ampelum.ComparativeBenchmarks/python-ref/` so dependency names
here cannot leak into production sources.

## Setup

### Preferred — uv

```bash
# install once: https://docs.astral.sh/uv/
cd tests/Ampelum.ComparativeBenchmarks/python-ref
uv sync   # resolves and locks via pyproject.toml; commits uv.lock the first time
```

### Fallback — bare python

```bash
cd tests/Ampelum.ComparativeBenchmarks/python-ref
python -m venv .venv
source .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

Then either ensure `python`/`uv` is on `PATH`, or set
`AMPELUM_COMPARATIVE_PY` to the absolute path of the interpreter (the C#
runner trusts that env var without probing).

## Wire protocol

`PythonDspyRunner` writes one JSON line to stdin and reads one JSON line
from stdout:

| Direction | Field | Type |
|-----------|-------|------|
| stdin     | `scenario` | string |
| stdin     | `examples` | int |
| stdin     | `mock_responses` | string[] |
| stdin     | `seed` | int |
| stdout    | `elapsed_ns` | int |
| stdout    | `peak_working_set_bytes` | int |
| stdout    | `prompt_tokens` | int |
| stdout    | `completion_tokens` | int |
| stdout    | `output` | string |

All scenario errors are caught inside `runner.py` and surfaced via the
`output` field (with a non-zero `output` prefix) so the C# side gets a
deterministic JSON line on stdout instead of a stderr traceback.

## Scenarios implemented in alpha.5

- `S8_bootstrap_fewshot_compile` — wall-clock to compile a
  `BootstrapFewShot` optimizer over N synthetic examples against a
  deterministic cycling mock LM.

Future scenarios (S10 RAG retrieval round-trip, S11 MIPROv2 compile,
S13 streaming-cancel propagation, S14 save/load, S15 dataset
materialisation) land in alpha.6 alongside `tools/comparative-rank.csx`.

## Why we shell out instead of pythonnet / clr-loaded interop

The Python reference's optimizer paths spawn their own threads and
mutate process-global state (LM client cache, `dspy.settings`). Loading
both runtimes in-process risks GIL/CLR thread-affinity deadlocks; the
subprocess boundary is the only sane way to keep the reference's
process-global state from poisoning consecutive BDN iterations.

## CI authority

`PythonDspyRunner.IsAvailable` returns `false` on any machine that
doesn't have `uv`, `python`, or `python3` on PATH. The `Sk` /
`AutoGenNet` / `LangChainNet` rows still produce a valid 4-competitor
table on those machines — the Python row is omitted gracefully. The
nightly comparative workflow (alpha.6) installs uv on the runner so the
Python row is mandatory in CI but optional locally.
