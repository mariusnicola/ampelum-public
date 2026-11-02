# SK Comparator Feature-Parity Audit — v1.0-rc1.alpha.4

Source-review only: no measurements, no build. Companion to
`docs/performance/comparative-results.md` (which reports the numbers).

This audit asks the orthogonal question: **is the SK comparator path
exercising an equivalent feature set to Ampelum's, or is it skipping
work that makes the headline ratios asymmetric?**

If SK skips the audit-trace capture, the typed-output parse, or the
hook/filter pipeline body, then "Ampelum-with-X vs SK-without-X"
overstates Ampelum's relative cost when Ampelum wins, and understates
the Ampelum gap when SK wins.

Two flavours of trustworthiness, both required before a number is
quotable at v1.0:

- **Harness-trustworthy** (existing audit) — the harness measures
  what it claims to measure (real `Kernel`, real `IFunctionInvocationFilter`,
  real `kernel.InvokePromptAsync`, no fake shim or fixture leak).
- **Feature-parity** (this audit) — the work each row performs is
  comparable; one side is not silently doing strictly less.

S1, S2, S4, S12 are all harness-trustworthy after rc1.alpha.4. They
are NOT all feature-parity. The verdicts below distinguish.

## Sources reviewed

- `tests/Ampelum.ComparativeBenchmarks/Competitors/Sk/SkSinglePredict.cs`
- `tests/Ampelum.ComparativeBenchmarks/Competitors/Sk/SkPipeline.cs`
- `tests/Ampelum.ComparativeBenchmarks/Competitors/Sk/SkStreaming.cs`
- `tests/Ampelum.ComparativeBenchmarks/Scenarios/Scenario01_SinglePredict.cs`
- `tests/Ampelum.ComparativeBenchmarks/Scenarios/Scenario02_PredictPlus4HookPipeline.cs`
- `tests/Ampelum.ComparativeBenchmarks/Scenarios/Scenario04_StreamingThroughput.cs`
- `tests/Ampelum.ComparativeBenchmarks/Scenarios/Scenario12_AllocationPerPredict.cs`
- `src/Ampelum/Predict.cs` (`ForwardAsync`, `StreamAsync`, `BuildOptions`)

## Feature parity table (per-call work performed by each row)

| Feature                                              | Ampelum row                                                                                                               | SK comparator row                                                                                          | Comparable? |
|------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|-------------|
| Typed input contract                                 | `[Signature]` + `[Input]` compile-time, `ProjectInputs` builds `IReadOnlyDictionary<string, object?>` per call            | `kernel.InvokePromptAsync("q")` — literal `string`, no `KernelArguments`, no `{{$var}}` substitution path  | **no** — SK passes a literal string; Ampelum projects a typed record |
| Prompt-template render                               | `ChatAdapter.Format` builds system+user `ChatMessage[]` from `[Signature]` instruction + per-field `[[ ## Name ## ]]` markers + demos | Trivial fast path. `InvokePromptAsync(string)` with no `{{$x}}` placeholders is a no-op render            | **no** — the doc-comment in `SkSinglePredict.cs` says "real SK template render"; in this harness it is the empty-substitution path |
| Hook / filter pipeline body (S2)                     | 4× `NoopHook` (default `ValueTask` returns), wrapped by `HookPipeline.RunBefore/RunAfter` + `HookContext` allocation + 4× `hookCtx.Set` (ModelId/PromptTokens/CompletionTokens/LmCallDurationMs) into `ConcurrentDictionary` + `Outputs` write | 4× `NoopFilter` (`=> next(context)`) — SK invokes them but the filter bodies touch nothing                | **partial** — both pipelines have the same depth-4 shape, but Ampelum's surrounding `Predict<TSig>` work injects 4 metadata writes per call regardless of hook bodies; SK's filter pipeline does not have an equivalent context-decoration layer |
| Audit / trace capture                                | `TraceRecord` constructed per call (Id, ModuleName, SignatureType, Inputs, Outputs, PromptMessages, RawResponse, Usage, Duration, Metadata), written to `ITraceSink` (`NullTraceSink` here, but the record is still built) | None. SK row returns `string` and discards everything else                                                  | **no** — Ampelum builds & writes a trace per call; SK builds nothing |
| Per-call observability (`ActivitySource` / counters) | `Diagnostics.Source.StartActivity("ampelum.module.forward")` + ~8 `SetTag` (`gen_ai.system`, `gen_ai.operation.name`, `gen_ai.semconv.version`, model id, response id, tokens, trace id) + `AddEvent` × 3-5 (`adapter.format`, `lm.call`, `adapter.parse`, `hook.before`, `hook.after`) + 4 histogram `Record` calls | None of the above. SK's `Kernel` may emit its own activities under its own source, but no GenAI semconv is wired through to the row | **no** — Ampelum pays for OTel emission even with no listener (Activity is null but the SetTag/Record paths still allocate KeyValuePair tag arrays) |
| `ChatOptions` clone semantics                        | `BuildOptions` returns `ctx.DefaultChatOptions` directly when adapter is not `IStructuredOutputAdapter` (no clone). For `ChatAdapter` (the path measured in S1/S2/S12) this is **no clone**       | `KernelArguments` not constructed by harness (literal `string` overload)                                  | **comparable, both light** — neither side allocates a clone in this row |
| Output parsing                                       | `ChatAdapter.Parse(_meta, response)` builds outputs `Dictionary<string, object?>`, then `RecordCloner.CloneWith(inputs, outputs)` constructs a typed `Prediction<TSig>` record | `result.GetValue<string>() ?? string.Empty` — raw concatenated text, no field extraction                  | **no** — Ampelum delivers structured typed output; SK delivers a string |
| Streaming parse path (S4)                            | Per-chunk `ChatAdapter.OnChunk` (marker-strip + field-route state machine) → `PredictionUpdate<TSig>` per chunk → terminal `Finalize` + `RecordCloner.CloneWith` + final `TraceRecord` | Per-chunk loop counts `StreamingKernelContent`; no parse, no terminal record                                | **no** — Ampelum runs the marker-strip parser per chunk; SK iterates raw |
| Retry-on-parse-failure loop                          | `RetryHook` integration in `ForwardAsync` — corrective-prompt retry loop, capped, audit-tagged                            | None                                                                                                       | **no** — Ampelum-only feature (not exercised in S1/S2/S4/S12 with `NoopHook`, but the dispatch code is on the hot path) |
| Deadline / `CheckDeadline` phase boundaries          | Six `CheckDeadline` calls per `ForwardAsync` (before-onbefore, post-onbefore, post-lm-call, post-parse, pre-onafter, post-onafter) | None                                                                                                       | **no** — Ampelum-only |

## Per-scenario verdicts

Verdict semantics:

- **comparable** — both sides exercise an equivalent feature set; numbers are load-bearing and quotable as-is at v1.0.
- **asymmetric, Ampelum favored** — Ampelum is doing strictly more work; the published number understates Ampelum's win (or Ampelum loses while doing more, which is still favorable to the published gap framing).
- **asymmetric, SK favored** — SK is doing strictly more work; published Ampelum win is overstated. (Not observed in current harnesses.)
- **incomparable** — feature delta is large enough that a direct ratio is misleading; the row should be reframed in release notes.

### S1 — single Predict, no hooks (Ampelum 778 ns / 4 064 B vs SK 1 410 ns / 5 344 B)

**Verdict: asymmetric, Ampelum favored.**

SK's row pays the SK Kernel + `KernelFunction` template-dispatch cost
and returns a string. Ampelum's row additionally:

- Projects a typed input record (`ProjectInputs`).
- Renders structured prompt with per-field markers (`ChatAdapter.Format`).
- Starts an `ActivitySource` activity with ~8 `gen_ai.*` tags.
- Records 4 histogram measurements (adapter format, LM call, adapter parse, module forward).
- Constructs a `TraceRecord` with `PromptMessages`, `RawResponse`, `Usage`, `Duration`, `Metadata`.
- Writes the trace to `ITraceSink` (Null here but the record is still built).
- Parses the structured response into a typed `Prediction<TSig>` via `RecordCloner.CloneWith`.

SK does none of these. Ampelum is **1.81× faster on mean and 1.31× lower
on alloc** while delivering a structured `Prediction<TSig>`, an audit
record, and OTel emission. The published win is real and **understated**.

Quotable at v1.0: **yes**, with the caveat phrasing "Ampelum is faster
than SK while also producing typed output, an audit record, and OTel
spans that SK's row omits."

### S2 — Predict + 4-hook pipeline (rc1.alpha.6: Ampelum 1.150 µs / 4.17 KB vs SK 1.817 µs / 5.63 KB)

**Verdict: asymmetric, Ampelum favored — and Ampelum now beats SK on BOTH mean (1.59×) and alloc (1.35×).** (rc1.alpha.4 result — Ampelum 1.40 µs / 6.76 KB vs SK 1.51 µs / 5.63 KB — preserved at the bottom of this section as the historical "must-be-#1 fail" baseline that the rc1.alpha.6 P2 fix closed.)

This is the interesting cell. Both are simultaneously true:

1. **Pipeline shapes match**: 4× `NoopHook` (returns `default ValueTask`)
   vs 4× `NoopFilter` (`=> next(context)`). At the filter-body level
   both pipelines are pass-through, so the noop-vs-noop comparison
   is fair as far as it goes.
2. **Ampelum's per-call work outside the hook bodies is heavier**.
   `Predict.ForwardAsync` unconditionally runs (when `Hooks.Count > 0`):
   - `new HookPipeline(hookList)` — pipeline alloc.
   - `new HookContext { ModuleName, Inputs }` — context alloc.
   - 4× `hookCtx.Set(HookMetadataKeys.ModelId | PromptTokens | CompletionTokens | LmCallDurationMs)` — `ConcurrentDictionary` writes (boxes `long`/`double`).
   - `hookCtx.Outputs = outputs` assignment.
   - `Diagnostics.HookBeforeDuration.Record` + `Diagnostics.HookAfterDuration.Record` — histogram emission with KeyValuePair tag arrays.
   - 6× `CheckDeadline` phase boundaries.
   - All the S1 work above (typed projection, format, parse, trace, activity tags).

   SK's `Kernel` filter pipeline does not have an equivalent
   context-decoration layer between filters. The 4 filters fire,
   the function invokes, the result is returned.

So: **Ampelum is doing strictly more work and still loses 20% on
allocation**. That is a real product gap, not a measurement artifact.
The asymmetry does not erase the gap; the gap does not erase the
asymmetry.

**File as v1.0-rc1 product task**: the four `hookCtx.Set` calls in
the success path are mandatory metadata for downstream hooks
(BudgetHook, AuditHook, RetryHook, LoggingHook) but they pay
`ConcurrentDictionary` insert + `long`/`double` boxing per call.
Candidates already enumerated under "v1.0-rc1 P2" in
`comparative-results.md`:

- Replace `HookContext._metadata = ConcurrentDictionary<…>` with
  a typed slot table (no boxing, no concurrent overhead — hook
  authors who relied on thread-safety are an unaudited risk surface).
- Cache the `HookPipeline` per `AmpelumContext` (one alloc, not
  one per `ForwardAsync`).
- Pool `HookContext` (needs a `Reset` and a bucket-stable
  `ConcurrentDictionary.Clear` — verify before changing).

Quotable at v1.0: **YES as an Ampelum-wins-S2 claim** (post rc1.alpha.6).
Honest framing for release notes:

> Ampelum's 4-hook pipeline runs **1.59× faster** (1.150 µs vs 1.817 µs)
> and **allocates 1.35× less** (4.17 KB vs 5.63 KB) than SK's 4-filter
> pipeline, while still delivering typed metadata fan-out (model id,
> token counts, LM call duration) into every hook — fan-out that SK's
> filter context does not equivalently expose.

**rc1.alpha.6 P2 fix (closed the historic 20% alloc gap):**

1. **Typed-slot HookContext** — replaced `ConcurrentDictionary<string,object?>`
   with four strongly-typed nullable backing fields for `ModelId`,
   `PromptTokens`, `CompletionTokens`, `LmCallDurationMs`. Eliminates
   `long`/`double` boxing on every Predict and removes the per-instance
   `ConcurrentDictionary` allocation. Other keys (e.g. `retry.attempt`,
   `budget.scope`) flow through a lazily-allocated `Dictionary` guarded
   by a per-instance lock — the published thread-safety contract for
   `Set` is preserved.
2. **Cached HookPipeline** — pre-sorted ascending/descending hook arrays
   are now keyed by reference identity of the `IReadOnlyList<IHook>`
   on the `AmpelumContext` via a `ConditionalWeakTable`. The pipeline
   is built exactly once per context, not once per `ForwardAsync`.

Net effect: −0.25 µs / −2.59 KB on the S2 row vs alpha.4. Coverage,
all 4 740 unit tests, and the 100%-line gate are preserved (the four
new typed properties + IReadOnlyDictionary projection have a dedicated
12-test fixture in `HookContextTests`).

**Historical baseline (rc1.alpha.4 source-review verdict, preserved):**
"Ampelum is doing strictly more work and still loses 20% on allocation"
— that gap is what the rc1.alpha.6 P2 fix targeted and closed.

### S4 — streaming throughput, ChunkCount=100 (Ampelum 21.95 µs vs SK 25.61 µs)

**Verdict: asymmetric, Ampelum favored.**

Ampelum's row runs the `ChatAdapter` streaming parse state machine
per chunk (`OnChunk` → marker-strip → field-route → `CompletedFields`
update), terminal `Finalize`, `RecordCloner.CloneWith`, terminal
`TraceRecord`, and yields typed `PredictionUpdate<TSig>` per chunk.

SK's row counts chunks (`int n = 0; await foreach (var _ in
kernel.InvokePromptStreamingAsync(question)) n++;`). No parse,
no terminal record, no typed update.

Ampelum is faster (~14% on mean) **while doing structured per-chunk
parse work that SK skips entirely**. The published win is understated.

Quotable at v1.0: **yes**, with phrasing "Ampelum streams and parses
into typed fields per chunk faster than SK iterates raw chunks."

### S12 — allocation per Predict (Ampelum 4 064 B vs SK 5 344 B)

**Verdict: asymmetric, Ampelum favored.** (Same delta as S1; S12
is the alloc-focused cut of the S1 measurement using the same
harness.)

Ampelum allocates 1 280 B less than SK while constructing a typed
`Prediction<TSig>`, a `TraceRecord`, and emitting OTel tags that
SK's row does not produce. **Win is understated.**

Quotable at v1.0: **yes**, same caveat as S1.

## What this means for the "1.81× faster" launch claim (S1 mean)

The bald number stands and is feature-asymmetric in Ampelum's favor.
The honest one-liner for release notes is:

> Ampelum is 1.81× faster than Semantic Kernel on a single
> structured Predict call (778 ns vs 1 410 ns) **while also producing
> a typed `Prediction<TSig>`, an audit `TraceRecord`, and OTel
> `gen_ai.*` spans that SK's `kernel.InvokePromptAsync` row does not
> emit**.

Drop the "while also" clause and you are quoting an asymmetric ratio
without the asymmetry — that's the failure mode this audit prevents.

## What this means for the S2 alloc cell (must-be-#1) — CLOSED at rc1.alpha.6

The rc1.alpha.4 audit said: *"Ampelum's `HookPipeline` + `HookContext`
+ per-call metadata fan-out costs ~1 KB more than SK's pass-through
filter chain… upgrades that work from 'nice to have' to 'must-fix
before quoting S2'."*

rc1.alpha.6 closed the gap. The typed-slot `HookContext` + cached
`HookPipeline` (commit history under `feat(hooks): typed-slot HookContext`
+ `perf(hooks): cache HookPipeline per AmpelumContext`) shaved
~2.6 KB off the S2 row, flipping the SK comparison: Ampelum now
allocates **1.35× less** than SK while delivering the typed metadata
fan-out the audit's "real distinction" caveat called out.

S2 is now load-bearing as a Ampelum-wins-on-everything cell.

## Cross-references

- `docs/performance/comparative-results.md` — verified ShortRun numbers + D1-D7 verdicts.
