"""Python reference runner for Ampelum.ComparativeBenchmarks.

Reads a single-line JSON request from stdin, dispatches to the named scenario,
writes a single-line JSON reply to stdout. Designed for the
`PythonDspyRunner` C# subprocess harness — keep the wire shape stable.

Request shape (matching `PythonDspyRunner.RunAsync`):
    {"scenario": str, "examples": int, "mock_responses": [str], "seed": int}

Reply shape (matching the `RunResult` C# record, snake_case):
    {"elapsed_ns": int, "peak_working_set_bytes": int,
     "prompt_tokens": int, "completion_tokens": int, "output": str}

Scenarios:
    - "S8_bootstrap_fewshot_compile" — wall-clock to compile a BootstrapFewShot
      optimizer over `examples` synthetic items against a deterministic
      mock LM that cycles `mock_responses`.

Failure mode: any exception is written as a JSON reply with `output` containing
the traceback so the C# side surfaces it via InvalidOperationException.
"""
from __future__ import annotations

import json
import os
import sys
import time
import traceback
from typing import Any


def _peak_rss() -> int:
    """Best-effort peak working set in bytes; 0 if psutil unavailable."""
    try:
        import psutil  # type: ignore[import-untyped]
        p = psutil.Process(os.getpid())
        # On Linux the high-watermark is `memory_info().rss` after sustained
        # allocation; on Windows the API exposes `peak_wset` directly.
        info = p.memory_info()
        return int(getattr(info, "peak_wset", info.rss))
    except Exception:
        return 0


def _scenario_s8(req: dict[str, Any]) -> dict[str, Any]:
    """BootstrapFewShot compile wall-clock against a cycling mock LM."""
    examples = max(1, int(req.get("examples", 50)))
    mock_responses = req.get("mock_responses") or ["42"]
    seed = int(req.get("seed", 0))

    import dspy  # type: ignore[import-not-found]

    class _Cycling(dspy.LM):
        """Deterministic LM stub: cycles through `mock_responses` per call."""

        def __init__(self, responses: list[str]) -> None:
            super().__init__(model="cycling-mock")
            self._responses = responses
            self._idx = 0
            self.history: list[Any] = []

        def __call__(self, *args: Any, **kwargs: Any) -> list[str]:
            r = self._responses[self._idx % len(self._responses)]
            self._idx += 1
            return [r]

        def basic_request(self, prompt: str, **kwargs: Any) -> dict[str, Any]:
            r = self._responses[self._idx % len(self._responses)]
            self._idx += 1
            return {"choices": [{"text": r}]}

    dspy.settings.configure(lm=_Cycling(list(mock_responses)))

    class _QA(dspy.Signature):
        """Answer the question."""

        question: str = dspy.InputField()
        answer: str = dspy.OutputField()

    program = dspy.Predict(_QA)
    trainset = [
        dspy.Example(question=f"q{i}", answer="42").with_inputs("question")
        for i in range(examples)
    ]

    def _metric(_gold: Any, _pred: Any, _trace: Any | None = None) -> bool:
        return True

    optimizer = dspy.BootstrapFewShot(metric=_metric, max_bootstrapped_demos=4, max_labeled_demos=16)

    t0 = time.perf_counter_ns()
    try:
        optimizer.compile(program, trainset=trainset)
    except Exception:  # noqa: BLE001 — we want to bubble the traceback below
        elapsed = time.perf_counter_ns() - t0
        return {
            "elapsed_ns": elapsed,
            "peak_working_set_bytes": _peak_rss(),
            "prompt_tokens": 0,
            "completion_tokens": 0,
            "output": "compile failed: " + traceback.format_exc(),
        }
    elapsed = time.perf_counter_ns() - t0

    _ = seed  # accepted but currently unused (S8 mock is deterministic by construction)

    return {
        "elapsed_ns": elapsed,
        "peak_working_set_bytes": _peak_rss(),
        "prompt_tokens": 0,
        "completion_tokens": 0,
        "output": f"bootstrap_compiled examples={examples}",
    }


_SCENARIOS = {
    "S8_bootstrap_fewshot_compile": _scenario_s8,
}


def main() -> int:
    raw = sys.stdin.readline()
    if not raw:
        sys.stdout.write(json.dumps({
            "elapsed_ns": 0, "peak_working_set_bytes": 0,
            "prompt_tokens": 0, "completion_tokens": 0,
            "output": "no stdin",
        }) + "\n")
        return 2

    try:
        req = json.loads(raw)
    except json.JSONDecodeError as e:
        sys.stdout.write(json.dumps({
            "elapsed_ns": 0, "peak_working_set_bytes": 0,
            "prompt_tokens": 0, "completion_tokens": 0,
            "output": f"bad json: {e}",
        }) + "\n")
        return 2

    scenario = str(req.get("scenario", ""))
    fn = _SCENARIOS.get(scenario)
    if fn is None:
        sys.stdout.write(json.dumps({
            "elapsed_ns": 0, "peak_working_set_bytes": 0,
            "prompt_tokens": 0, "completion_tokens": 0,
            "output": f"unknown scenario: {scenario}",
        }) + "\n")
        return 2

    try:
        reply = fn(req)
    except Exception:  # noqa: BLE001
        reply = {
            "elapsed_ns": 0, "peak_working_set_bytes": 0,
            "prompt_tokens": 0, "completion_tokens": 0,
            "output": "scenario raised: " + traceback.format_exc(),
        }

    sys.stdout.write(json.dumps(reply) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
