# Changelog

All notable changes to **Ampelum** are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versioning follows [SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased] — v1.0-rc1.alpha.12

### Audit — EventType tamper detection restored via SchemaVersion (closes alpha.11 trade-off)

- **New `AuditEntry.SchemaVersion` property** (default `1`). Selects the
  hash algorithm used to compute and verify `EntryHash`:
  - `SchemaVersion = 1` — alpha.5 shape, `EventType` excluded.
  - `SchemaVersion = 2` — alpha.12 shape, `EventType` folded into the
    canonical bytes between `ModuleName` and `ModelId` (NUL-separated).
- **`AuditChainHasher.ComputeV2`** — internal v2 hash routine. Identical
  layout to v1 but with `EventType` interleaved.
- **`AuditHook.OnAfterAsync`** — new rows ship as `SchemaVersion = 2`
  with `EventType` in the chain hash. Tampering with `EventType` on a
  v2 row is now caught by `AuditChainVerifier.VerifyAsync`, restoring
  the integrity coverage that alpha.11 traded away.
- **`AuditChainVerifier.VerifyAsync`** dispatches per-row on
  `entry.SchemaVersion`. Mixed v1 + v2 chains validate cleanly: each row
  recomputes under its own algorithm.
- **Migration:** the C# default for `SchemaVersion` is `1`, so legacy
  serialized rows (no field) deserialize as v1 and verify under the
  legacy algorithm without a custom converter. New rows explicitly
  assign `2`. No row rewriting; no breakage of pre-alpha.12 chains.
- **No `[Experimental]` on `SchemaVersion`** (deviation from initial
  task spec): the alpha.10 → alpha.11 lesson is that STJ source-gen on
  `AuditEntryJson` propagates the diagnostic across the audit
  serialization path. The property is listed under
  `Ampelum.Audit.AuditEntry.SchemaVersion` in `PublicAPI.Unshipped.txt`
  as the surface-stability marker; the property attribute stays plain.
  Documented in `docs/security/audit-replay-hash.md`.
- **Tests:** new tamper-detection tests on `AuditChainVerifierTests`
  (`Verify_V2Row_TamperedEventType_DetectedByEntryHash`,
  `Verify_V1Row_NoEventTypeInHash_VerifiesAtDefault`,
  `Verify_MixedV1AndV2Chain_Verifies`,
  `Verify_V2Row_GoodChain_Verifies`); new hasher coverage on
  `AuditChainHasherTests`
  (`ComputeV2_DiffersFromV1_AndDetectsEventTypeChange`,
  `ComputeV2_PrevHashWrongLength_Throws`,
  `ComputeV2_NonPrimitive_ThrowsAuditSerializationException`,
  `ComputeV2_Deterministic_GivenSameInputs`).

## v1.0-rc1.alpha.11

### Audit — `[Experimental]` consistency on ReplayHash (alpha.10 follow-up)

- **`AuditEntry.ReplayHash` removed.** alpha.10 added the field directly
  on `AuditEntry` but had to drop the `[Experimental("AMPELUM_V09_PREVIEW")]`
  attribute because the STJ source generator on `AuditEntryJson` propagated
  the diagnostic to every consumer of the JSON serialization context.
  Marker enforcement was lost; documentation in xmldoc was the only signal.
- **New `Ampelum.Audit.AuditEntryReplay` static class** (extension method).
  `entry.GetReplayHash()` derives the input-side hash on demand from
  `(ModuleName, EventType, ModelId, Inputs)`. The class is
  `[Experimental("AMPELUM_V09_PREVIEW")]`-attributed; living off the
  serialized type means STJ source-gen does not see the marker, so it can
  be enforced consistently across the codebase without `<NoWarn>` in
  library code.
- **`AuditChainVerifier.VerifyAsync`** no longer ranges over `ReplayHash`
  (there is no stored value to compare against). `EntryHash` and
  `PrevHash` linkage validation are unchanged. Trade-off: `EventType`
  tampering is no longer caught by the chain verifier — `EntryHash`
  covers `ModuleName`/`ModelId`/`Inputs` but `EventType` was deliberately
  excluded from `EntryHash` (alpha.5 Gap 2) for replay determinism.
  Promoting `EventType` into `EntryHash` at v1.0 tag time (with a
  row-shape migration) is the follow-up that re-establishes full chain
  coverage. Documented in `docs/security/audit-replay-hash.md`.
- **`AuditHook.OnAfterAsync`** no longer sets a `ReplayHash` field.
  `AuditChainHasher.ComputeReplay` (internal) is unchanged and still
  backs the new accessor.
- **Test updates:** `Pareto05_StreamingForward_Fake` and
  `AuditHookRecordModeTests.OnAfter_PopulatesReplayHash_DeterministicOnSameInputs`
  now call `entry.GetReplayHash()`. Three verifier tests
  (`Verify_PopulatedReplayHash_Validated`, `Verify_TamperedReplayHash_Detected`,
  `Verify_EmptyReplayHash_TreatedAsNotAsserted`) were deleted — they
  asserted stored-field invariants that no longer exist.
- **`tests/ConsumerSmoke.Tests/ConsumerSmoke.Tests.csproj`:** removed the
  stale `AMPELUM_V09_PREVIEW` `<NoWarn>` entry that was added in alpha.10
  when `ReplayHash` was a serialized property; consumers no longer hit
  the diagnostic from STJ source-gen propagation.

## [v1.0-rc1.alpha.10] — 2026-04-26 — Consumer-smoke pack hardening + ReplayHash (Gap 1 closed)

### Packaging (community-blocking pack-metadata fixes)

- **All 19 packs now ship at `0.8.0-rc1`.** Previously only `Ampelum` core
  was prerelease; the 14 sister packs (`Ampelum.Testing`, all `Ampelum.Tools.*`,
  `Ampelum.VectorStores.*`, `Ampelum.Observability.*`, `Ampelum.Mcp`,
  `Ampelum.Repl`, `Ampelum.Retrieval.Reranking`) declared a stable dep
  `Ampelum >= 0.8.0` in their nuspec, but only `Ampelum 0.8.0-rc1` ever
  shipped. A community user installing any sister pack hit
  `error NU1605: Detected package downgrade: Ampelum from 0.8.0 to 0.8.0-rc1`
  and could not restore. Fix moves the version into `Directory.Build.targets`
  so every pack inherits `0.8.0-rc1`. Net-new public surface unchanged.
- **`Ampelum.Analyzers` and `Ampelum.SourceGenerator` packs now carry full
  metadata.** Without `<IsPackable>true</IsPackable>` in their csprojs, the
  shared NuGet metadata block in `Directory.Build.targets` was skipped, so
  these packs shipped as `1.0.0` (default MSBuild Version) without
  `<license>`, `<repository>`, or readme. Strict feed validators would have
  rejected them. Both csprojs now opt in explicitly.

### Tests (consumer-smoke)

- **`tests/ConsumerSmoke.Tests/`** — sibling test project deliberately
  outside the coverage gate and the repo's central package management.
  Restores from `_packs/` via a scoped `nuget.config`, references the same
  five packs a downstream consumer would reach for, and runs four xUnit
  smoke tests:
  - `HelloPredictSmoke` — `Predict<TSig>` end-to-end via `FakeChatClient`.
  - `SourceGenSmoke` — `[Signature]` declared in the consumer assembly
    produces source-generated metadata and `SignatureMetadataCache.For<T>`
    resolves it.
  - `AnalyzerPackSmoke.AnalyzerPack_ShipsAtCanonicalPath` — verifies
    `analyzers/dotnet/cs/Ampelum.Analyzers.dll` is present in the published
    nupkg layout.
  - `AnalyzerPackSmoke.PackedAnalyzer_FiresAmp1010_OnFreeFormWithoutOptIn`
    — extracts the analyzer DLL from the nupkg, runs it against an
    in-memory `CSharpCompilation`, and asserts AMP1010 fires as a Warning.
- See `docs/release/consumer-smoke.md` for the full exercise log.

### Features (Gap 1 — streaming↔non-streaming audit equivalence)

- **`AuditEntry.ReplayHash`** — net-new public field. SHA-256 (lowercase
  hex) of the canonical input-side projection
  `(ModuleName, EventType, ModelId, Inputs canonical)`. Excludes Outputs,
  token counts, Sequence, PrevHash, and Timestamp by design — those are
  LM-nondeterministic or chain-position metadata and would defeat the
  cross-call equivalence purpose. Default value is the empty string;
  legacy persisted rows are forward-compatible.
- **`AuditChainHasher.ComputeReplay`** (internal) computes the value;
  `AuditHook.OnAfterAsync` populates it on every entry it appends.
- **`AuditChainVerifier.VerifyAsync`** now recomputes and validates
  `ReplayHash` per row when populated; reports
  `"ReplayHash does not match recomputed value."` on mismatch. Empty
  `ReplayHash` is treated as "not asserted" so legacy chains verify clean.
- **`Pareto05_StreamingForward_Fake`** (`Ampelum.IntegrationTests`) — new
  fake-LM Pareto05 variant that promotes the streaming↔non-streaming
  equivalence relation to a real assertion:
  `streamEntries[0].ReplayHash.ShouldBe(nonStreamEntries[0].ReplayHash)`.
  Runs in CI without an OpenRouter key. The original real-LM Pareto05
  keeps its weaker tuple-equality relation — LM nondeterminism prevents
  asserting `Outputs` equality there.
- See `docs/security/audit-replay-hash.md` for the design rationale,
  including why option (b1) — input-side hash — was chosen over
  literal-spec option (b2) and why the `[Experimental]` attribute was
  dropped from `ReplayHash`.

### Notes

- `Ampelum.Tests` line coverage on `[Ampelum]*` is **99.33%** at this
  alpha. This baseline pre-dates alpha.10 (verified by stashing changes
  and re-running on tip-of-`main`); the gaps live in async state machines
  on `Realtime.WebSocketTransport` and similar surfaces that have no unit
  test path. The new audit code paths shipped this alpha are at 100% line
  coverage. Coverage gate restoration is tracked separately.

## [v1.0-rc1.alpha.9] — 2026-04-26 — Cookbook 16→20: spec target reached

### Docs

- **Cookbook 16 → 20 recipes** — the four final cookbook recipes ship,
  reaching the v1.0 spec target. All 20 are extracted by the recipe-runner
  (`tests/Ampelum.Recipes.Tests`) and compile + execute as `[Fact]` tests.
  - `17-rag.md` — `EmbeddingRetriever` + `InMemoryVectorStore` + `FakeEmbeddingGenerator`
    over a 3-doc corpus, top-2 retrieval, then a chained `Predict<MathQa>`
    that consumes the retrieved context.
  - `18-hook-composition.md` — `BudgetHook` + `AuditHook` + custom
    `RecipeLoggingHook` composed on a single `AmpelumContext`; second
    scenario demonstrates `HookContext.Outputs` short-circuit via a
    `RecipeShortCircuitHook` with `Order = -1` that suppresses the LM call.
  - `19-source-generator.md` — runnable demo of `SignatureMetadataCache.For<TSig>()`
    populated by the source generator's reflection fallback path; documents
    `[assembly: GenerateSignatureMetadata]` opt-in and the AOT-strict
    `RegisterAccessors` delegate path.
  - `20-json-adapter.md` — `JsonAdapter` end-to-end with `Predict<MathQa>`:
    well-formed `{"answer":"42"}` happy path, malformed-response
    `AdapterParseException` path, and `JsonAdapter(strict: false)` lenient
    extraction from surrounding prose.

### Internal

- `tools/Ampelum.RecipeExtract/Program.cs` — added `using Ampelum.Retrieval;`
  and `using Ampelum.Exceptions;` to the generated test-file preamble so
  recipes 17 + 20 compile.
- `tests/Ampelum.Recipes.Tests/Fixtures.cs` — net-new
  `RecipeLoggingHook` and `RecipeShortCircuitHook` types used by recipe 18.

## [v1.0-rc1.alpha.8] — 2026-04-26 — Gap 5 IBudget.TryReserve + Stryker baseline run

### Features (Gap 5 — final open product gap closes)

- **`IBudget.TryReserve(calls, tokens, costCents)` + `Release(...)`** ship as
  default-interface methods with atomic semantics on `BudgetImpl` and a
  snapshot-only fallback for third-party impls. Closes the Pareto #11
  pre-LM short-circuit window where N parallel forwards all observed
  `CallsUsed=0 < K`, dispatched a real LM call apiece, and only had
  `Consume` reject the (K+1)-th completion.
- **`BudgetHook.OnBeforeAsync`** now reserves expected tokens via
  `TryReserve` and throws `BudgetExceededException` BEFORE any LM call on
  refusal. `OnAfterAsync` consumes actuals and releases the reservation;
  `OnErrorAsync` releases without consuming. Caller-supplied estimates via
  new `HookMetadataKeys.Expected{Prompt,Completion,Cost}` keys; defaults
  configurable on the hook ctor (500 prompt + 500 completion baseline).
- Pareto #11 integration test bumped to **N=8 / K=3** with chain-length-K
  audit invariant (was N=4 / K=2 with chain-length-N).
- New public surface lands in `PublicAPI.Unshipped.txt` under
  `[AMPELUM_V09_PREVIEW]`; existing `IBudget.Consume` API unchanged.

### Quality

- **Stryker mutation-testing baseline run.** First local Stryker sweep
  against `Ampelum` + `Ampelum.Testing` documented in
  [`docs/performance/mutation-testing.md`](docs/performance/mutation-testing.md),
  including two Windows-specific blockers (Stryker 4.4.1 compiler-version
  mismatch with `[GeneratedRegex]`; solution-mode test-project auto-discovery
  conflicting with the un-built `Ampelum.Recipes.Tests/Program.cs`) and the
  project-mode workaround. Score will be appended in the post-tag follow-up
  as the run completes (4797 tests under mutation, ~30–60 min wall-clock).

## [v1.0-rc1.alpha.7] — 2026-04-26 — closeout: core NuGet pack + cookbook 7→16 recipes

### Packaging

- **Ampelum core NuGet pack closed.** G4 deferred core pack (deferred at
  alpha.6 because the S2 typed-slot edits were mid-flight on PublicAPI)
  now ships clean: `Ampelum.0.8.0-rc1.nupkg` (322 KB), README + LICENSE +
  Ampelum.dll (638 KB) + xml-doc (440 KB), 15 pinned deps, no source-gen
  artifacts, no test-only deps. Core pins to `0.8.0-rc1` because of beta
  `Lucene.Net 4.8.0-beta00017` (NU5104); sister packs stay at `0.8.0`
  stable since none transitively pull Lucene.
- `Directory.Build.targets` makes `<Version>` overridable per-csproj and
  declares `<RepositoryType>git</RepositoryType>` explicitly per the G4
  sign-off checklist.

### Documentation

- **Cookbook expanded 7 → 16 recipes** (against the 20-recipe brief; the
  original brief items 08/10/12/13 duplicate the already-shipped 02-05,
  so the 9 net-new recipes cover the remaining surface):
  ReAct + tool, optimizer compile, rate limit, semantic cache, routed
  failover, group chat handoff, AOT publish (prose-only stub — out of
  process), evaluate harness, persistence round-trip. Recipe extractor
  now imports the broader namespace set required by the new recipes
  (`Agents`, `Caching.Semantic`, `Optimizers`, `Persistence`,
  `RateLimiting`, `Routing`, `Microsoft.Extensions.AI`) and opts into
  the `AMPELUM_V09_PREVIEW` experimental surface used by group-chat,
  rate-limit, and routing recipes. All 16 recipes compile and pass.

## [v1.0-rc1.alpha.6] — 2026-04-26 — S2 alloc parity (and reversal) vs SK

### Performance

- **v1.0-rc1 P2: S2 alloc parity with SK — 4 270 B/op (was 6 920 B).**
  Closed the must-be-#1 cell that lost 20% on alloc through alpha.4.
  Ampelum's S2 4-hook pipeline now runs at **1.150 µs / 4.17 KB**
  (down from 1.40 µs / 6.76 KB) — beating SK's 1.817 µs / 5.63 KB on
  BOTH mean (1.59×) and alloc (1.35×) while preserving typed-metadata
  fan-out (`ModelId`/`PromptTokens`/`CompletionTokens`/`LmCallDurationMs`)
  into every hook. Two changes:

  - **Typed-slot `HookContext`.** Replaced the per-context
    `ConcurrentDictionary<string,object?>` with four strongly-typed
    nullable backing fields for the well-known LM-result keys
    (eliminates `long`/`double` boxing and the dictionary alloc).
    Other keys flow through a lazily-allocated `Dictionary` guarded by
    a per-instance lock — `Set` thread-safety contract preserved.
    Net-new typed properties (`ModelId`, `PromptTokens`,
    `CompletionTokens`, `LmCallDurationMs`) are additive public API.
  - **Cached `HookPipeline`.** Pre-sorted ascending/descending hook
    arrays are now cached via `ConditionalWeakTable` keyed by the
    `IReadOnlyList<IHook>` reference on `AmpelumContext` — built once
    per context, not once per `ForwardAsync`.

  S1/S12 unchanged (no hooks → typed-slot path is dead code there).
  4 740 unit tests pass; coverage unchanged from baseline.

  Docs: `docs/performance/comparative-results.md` (S2 table revised),
  `docs/performance/sk-feature-parity.md` (S2 verdict flipped from
  "NO as Ampelum-wins-S2" to "YES, Ampelum beats SK on both axes").

## [Unreleased] — 2026-04-26 — v1.0-rc1 readiness gates G2–G6

Non-tagged, parallel work to the in-flight S2 alloc fix; each gate landed
as its own commit. Tag `v1.0-rc1.alpha.6` is owned by the S2-fix track and
will fold these gates into its release notes.

### Added

- **G2 — Stryker mutation gate ≥85%.** Broaden `stryker-config.json` from
  Optimizers/Audit/Hooks to all of `[Ampelum]*` + `[Ampelum.Testing]*`;
  promote `thresholds.break` from 0 (informational) to 85 (CI gate). New
  `.github/workflows/mutation-nightly.yml` fails the build below 85% and
  archives HTML+JSON 30 days. Doc: `docs/performance/mutation-testing.md`.
- **G3 — Threat model refresh.** Front-matter milestone v0.7→v1.0-rc1; extend
  STRIDE coverage from 9 to 13 scenarios for v0.9 surface (multi-agent
  handoff impersonation, audit-sink durability vs crash window, semantic-cache
  cross-call leak, rate-limiter / circuit-breaker DoS amplification). README
  links the threat model under a new Security section.
- **G4 — NuGet publish dry-run.** 18 of 19 packable libraries produce valid
  `.nupkg` with required metadata; the 19th (`Ampelum` core) is deferred until
  the parallel S2-fix lands `v1.0-rc1.alpha.6`. Patches `src/Ampelum.SourceGenerator.csproj`
  to use the analyzer-pack idiom (was failing NU5128). Doc:
  `docs/release/nuget-dryrun.md` (procedure, inventory, sign-off checklist).
- **G5 — Template CI smoke.** New `.github/workflows/template-smoke.yml`
  runs `dotnet new install` + `dotnet new <persona>` + `dotnet build` for
  each of the 5 persona templates (`ampelum-{agent,backend,enterprise,platform,researcher}`).
  Existing `tests/Ampelum.Templates.Tests` (25 tests, all green) cover
  source-shape; this gate adds end-to-end instantiation.
- **G6 — Migration guides + recipe cookbook (scope-reduced).** Four migration
  guides (`docs/migration/from-{semantic-kernel,meai-raw,dspy-py,langchain}.md`),
  each with before/after pairs for Predict, ChainOfThought, ReAct, optimizer,
  streaming. Recipe cookbook grows from 1 to 7 (chain-of-thought, streaming,
  budget-cap, audit-chain-validation, retry-on-parse-failure,
  trace-sink-inspection joined the existing hello-predict). All 7 compile
  through `tests/Ampelum.Recipes.Tests` and pass green.

### Changed

- **Recipe extractor robustness.** `tests/Ampelum.Recipes.Tests.csproj`
  switched from `dotnet run` (publishes a self-contained `.exe` that gets
  blocked by Windows Application Control / WDAC policies) to `dotnet exec`
  against the dll. The extractor template now emits `#nullable enable` and
  imports `Ampelum.Audit` / `Ampelum.Budget` / `Ampelum.Hooks` so recipes
  needing those namespaces compile without per-recipe `using` lines.

### Deferred to alpha.7

- **G6 cookbook tail (13 of 20).** ReAct + tool, optimizer compile,
  rate-limiter, semantic cache, multi-provider routing, group chat, AOT
  publish, evaluation harness, retrieval, persistence round-trip, hook
  pipeline composition, source generator output, JSON adapter — each requires
  type fixtures or surface-area review beyond the scope of a parallel-agent
  session. Tracked for v1.0-rc1.alpha.7.
- **G4 core pack** — blocked by S2-fix mid-edit on `IHook.cs` /
  `Predict.cs`; reruns clean as soon as `PublicAPI.Unshipped.txt` rebalance
  lands with alpha.6.
- **G2 first kill-score** — captured in the first nightly run after merge;
  if below 85%, survivors triaged before v1.0 GA (current narrow-scoped
  config historically ran clean, so a 5–15% gap is the realistic surprise
  surface).

## [1.0-rc1.alpha.5] — 2026-04-26 — v1.0-rc1 P5: six product gaps closed

**v1.0-rc1 product-correctness task.** Closes six gaps surfaced by the alpha.3/alpha.4
Pareto and parity work, with explicit Pareto promotions per gap.

### Added

- **`AuditEntry.EventType` discriminator** (Gap 2). Net-new `string EventType`
  property, defaults to `"forward.completed"` so the existing row shape is
  preserved across reads of legacy serialized data. Hooks publish via
  `HookContext.Metadata[AuditMetadataKeys.EventType]`; `AuditHook` propagates onto
  the appended entry. New `AuditMetadataKeys.EventType` and
  `AuditMetadataKeys.EventTypeAgentHandoff` constants. Pareto #11 + #14 promoted.
- **`GroupChat` agent-handoff audit emission** (Gap 3). New 4-arg ctor accepts an
  optional `IAuditSink`; when supplied, every speaker selection (selector- or
  explicit-handoff-driven) emits one `AuditEntry` with EventType
  `"agent.handoff"`, Inputs = {source, target, turn, kind}, Outputs =
  {conversation length, state hash}. Rows are hash-chained the same way Predict's
  AuditHook chains its rows. Pareto #18 promoted with end-to-end chain validation.
- **`BatchedAuditSink` "When to use" framing** (Gap 6). xmldoc clarifies batched
  amortisation guidance: I/O-bound inner sink + batch≥256 wins; in-memory
  inner or sub-16-producer workloads add channel overhead. Capacity-sizing rule
  of thumb (4× concurrent producers).

### Changed

- **Hook-phase span tags** (Gap 4 / G2 spec). The hook before/after/error
  durations recorded into `Diagnostics.HookBeforeDuration` /
  `HookAfterDuration` are now ALSO attached to the active Activity as
  `ampelum.hook.<phase>.duration_us` span tags (long microseconds). The existing
  `ampelum.hook.before` / `ampelum.hook.after` ActivityEvents are preserved for
  back-compat; the new tags let OTel exporters graph hook latency without
  parsing event timestamps. Pareto #17 promoted to assert tag presence.

### Documented (no behavioural change)

- **Streaming↔non-streaming audit equivalence relation** (Gap 1). Pareto #5
  promoted to assert the relation that holds across paths: `(Inputs canonical,
  ModuleName, EventType)` equal AND each side emits exactly one row with
  parsed Outputs. EntryHash equality is intentionally NOT asserted —
  `AuditEntry.Timestamp` (UtcNow at OnAfter) and underlying LM nondeterminism
  (token counts, exact answer text) make EntryHash divergence a feature, not a
  bug.
- **Pareto #11 N=4/K=2 retained** (Gap 5). Master plan asked for N=64/K=20 but
  `BudgetHook.OnBefore` only consults a snapshot — under N parallel calls all
  observe `CallsUsed=0 < K` simultaneously. Atomic pre-LM reservation requires
  either an `IBudget.TryReserve` API or call-pre-counting that breaks transient-error
  semantics; both judged out of scope for alpha.5. Tracked for v1.0 GA.

### Test delta

+ Pareto #5 / #11 / #14 / #17 / #18 promoted to real assertions.
+ Audit unit tests: `AuditEntry.EventType` default + override; AuditHook
  EventType propagation (explicit, default, wrong-type, empty);
  `AuditMetadataKeys` new constants.
+ GroupChat unit tests: per-turn handoff emission, prev-hash chain, unknown-target
  row write-before-termination.

## [1.0-rc1.alpha.3] — 2026-04-26 — v1.0-rc1 P3: three Pareto-surfaced product gaps closed

**v1.0-rc1 product-correctness task.** Closes three gaps surfaced by the alpha.2
Pareto integration tests that previously required workarounds (shims, manual
re-drive, missing audit symmetry).

### Added

- **`JsonProgramPersistence.LoadPredictAsync<TSig>(path, AmpelumContext, ct)`**
  (Gap 1) — ergonomic overload that bypasses the `where TProgram : class, new()`
  constraint of `LoadAsync<TProgram>`. Returns `OptimizedProgram<Predict<TSig>>`
  seeded with a `Predict<TSig>` constructed from the supplied context. Marked
  `[Experimental("AMPELUM_V09_PREVIEW")]`. Pareto #6 promoted from `QaProgram`
  shim form to direct `Predict<QaSig>` use.

### Changed

- **`Predict.ForwardAsync` auto-loops on `RetryHook.corrective_prompt`** (Gap 2).
  When parse fails with `AdapterParseException` and the hook pipeline publishes
  `retry.recovered=true` + a non-empty `retry.corrective_prompt`, ForwardAsync
  appends the corrective text as a follow-up user message and re-issues the LM
  call up to `RetryHook.maxRecoveryAttempts` times (capped by `RecoveryAttemptsCeiling=5`).
  Successful recoveries record `retry.attempt = N` for audit-chain provenance.
  Pareto #12 promoted from manual second-forward to single-call auto-recovery.
- **`Predict.StreamAsync` participates in the hook pipeline** (Gap 3). OnBefore
  runs once before the LM streaming call; OnAfter runs once after Finalize with
  `hookCtx.Outputs` populated and `lm.model_id`/`lm.tokens.prompt`/`lm.tokens.completion`/`lm.call.duration_ms`
  set for parity with Forward. OnError fires on any terminal error. PSQ-11
  short-circuit honoured (OnBefore writing `Outputs` skips the LM stream).
  Per-chunk hook fan-out intentionally not wired (would be too chatty without
  audit-chain value). Pareto #5 + #9 promoted to assert audit-entry parity
  across streaming and non-streaming.

### Test delta

- Unit: 4708 → 4718 (+10) — three `PredictRetryLoopTests`, three streaming
  hook-participation tests in `PredictStreamingTests`, four
  `LoadPredictAsync*` tests in `JsonProgramPersistenceTests`.
- Integration: 26 → 26 (same count; Pareto #5/#6/#9/#12 promoted from
  documented-gap shape to direct-assertion shape).

## [1.0-rc1.alpha.2] — 2026-04-26 — v1.0-rc1 P2: parallel scaling reframe (no contention found)

**v1.0-rc1 perf-2 task.** Discharges the alpha.6 "M6 N=64 = 64.57× FAIL D5"
finding by showing it was a bench-design artifact, not product-code
contention.

### Changed — S9 N-parallel scaling benchmark reframe

- **`MockChatClient.GetResponseAsync` returned `Task.FromResult(_cached)`.**
  On the fan-out shape (`for i: tasks[i] = ForwardAsync(); WhenAll(tasks);`),
  every `await` in `ForwardAsync` saw an already-completed task and continued
  synchronously on the caller's thread. All N "parallel" forwards therefore
  ran sequentially on one thread; the linear N× wall-clock was N sequential
  calls, not contention.
- **New `YieldingMockChatClient` for S9** (`tests/Ampelum.ComparativeBenchmarks/Competitors/Shared/`):
  awaits `Task.Yield()` before returning the canned response so concurrent
  forwards actually distribute across threadpool workers. Other scenarios
  continue to use the original sync `MockChatClient` (S1/S2/S6/etc don't fan
  out; sync mock minimises noise floor).
- **New permanent `EmptyAsync` benchmark** in `Scenario09_NParallelScaling`:
  each task does only `Task.Yield()`, no Ampelum code. This is the
  threadpool/`Task.WhenAll` harness floor at the chosen N. The metric to
  read is `Ampelum/EmptyAsync` at the same N, NOT `Ampelum N=64 / Ampelum N=1`
  (which is dominated by harness fan-out cost).

### Verified — flat ~4× ratio across N=1..64 (no contention)

Per-process BDN ShortRun, fresh process per N, Windows dev box, 32 cores
(2026-04-26):

| N  | Ampelum (ns) | EmptyAsync (ns) | Ampelum/EmptyAsync |
|----|-------------:|----------------:|-------------------:|
| 1  |        1,789 |             580 |             3.08×  |
| 8  |        8,588 |           2,023 |             4.25×  |
| 32 |       32,841 |           8,007 |             4.10×  |
| 64 |       63,050 |          15,740 |             4.01×  |

The `Ampelum/EmptyAsync` ratio is flat at ~4× — Ampelum scales as well
as the threadpool itself. Previous "64.57× FAIL" withdrawn.

### Suspect list ruled out

The dispatch listed seven candidate contention sources. Investigation
showed none apply on the S9 hot path:

1. `HookPipeline` — not constructed when `resolved.Hooks is null` (S9's case).
2. `ChatAdapter.Format/Parse` — purely stateless static helpers.
3. `Diagnostics.ActivitySource.SetTag` — no listener → returns null Activity → SetTag is a no-op.
4. `ChatOptions.Clone()` — only called for `IStructuredOutputAdapter`; `ChatAdapter` isn't one.
5. `MockChatClient` shared instance — no internal state to contend on.
6. `AmpelumContext` — constructor-injected, reused across all forwards (not per-call alloc).
7. `inputsDict` build — uses source-generated accessor delegates (no reflection on hot path for `[Signature]`-decorated types).

### Updated

- `docs/performance/comparative-results.md` — M6 row + §M6 reframe section
  + B3 row updated. Withdraws the "FAIL D5 ≤1.5×" verdict; replaces with
  flat ~4× Ampelum/EmptyAsync across N → no contention.
- `tools/measure-parallel-scaling.sh` — verdict logic reframed; the
  `Ampelum N=64 / Ampelum N=1` ratio is preserved for continuity but
  flagged as harness-floor-dominated; the authoritative metric is now
  `Ampelum/EmptyAsync` at the same N.

### What is NOT in this release

No product-code changes. The `4×` per-task overhead vs harness floor is
genuine (M2 alloc cost expressed in time) and stays on the v1.0-rc1
backlog as a throughput target, separate from the (now-discharged)
contention concern.

---

## [1.0-rc1.alpha.1] — 2026-04-26 — v1.0-rc1 P1: Predict allocation hot path

**v1.0-rc1 perf-1 task.** Reduces `Predict<TSig>.ForwardAsync` per-call
allocations by removing three certain hot-path allocators identified by
source review against the v0.9.1-alpha.6 verified BDN baseline. Re-runs
S1 + S2 ShortRun on the same Windows dev box to measure the delta.

### Changed — Predict allocation reduction

- **`Stopwatch.StartNew()` → `Stopwatch.GetTimestamp()` + `GetElapsedTime`**
  on the outer Forward wall-clock, the LM-call sub-phase, and the Stream
  outer wall-clock. The class-typed `Stopwatch` allocates per call;
  the value-typed `long` timestamp pair does not. Pattern was already
  used for adapter/hook sub-phases in the same method.
- **`ILogger<Predict<TSig>>` cache.** `LoggerFactory.CreateLogger<T>()`
  allocates a `Logger<T>` per call. Cache one per `Predict<TSig>`
  instance keyed by factory identity (the universal case).
- **Lazy metadata `Dictionary<string, object?>` allocation.** The
  TraceRecord success path always allocated a new dict and populated
  it conditionally. Now allocated only when at least one entry will be
  added (cache_hit OR demos.Count > 0). When neither fires, an
  immutable `EmptyMetadata` sentinel satisfies the `IReadOnlyDictionary`
  contract on `TraceRecord.Metadata`. Same fix on the `WriteTrace`
  error path.

### Measured (Windows dev box, BDN ShortRun, IterationCount=3, WarmupCount=3)

| Scenario | Before | After | Δ | D3 budget | Residual |
|----------|--------|-------|---|-----------|----------|
| **S1 — single Predict, no hooks** | 4440 B | **4064 B** | **−376 B (−8.5%)** | ≤512 B | **7.94× over** (was 8.66×) |
| **S2 — Predict + 4-hook pipeline** | 7296 B | **6920 B** | **−376 B (−5.2%)** | ≤1024 B | **6.76× over** (was 7.13×) |
| S1 mean | 895.99 ns | 760.25 ns | −135.74 ns (−15%) | n/a | n/a |
| S2 mean | 1507.34 ns | 1407.63 ns | −99.71 ns (−7%) | n/a | n/a |

### Residual to D3 — what was NOT fixed and why

D3 budget is **NOT yet reached**. The remaining ~3.5 KB on S1 and
~6.4 KB on S2 above budget come from sites that require either an
adapter API change or restructuring tracing semantics:

- **`ChatAdapter.Format` (~2-3 KB):** builds the rendered prompt
  `StringBuilder`, the per-field marker strings, and the
  `List<ChatMessage>`. The list is captured by `TraceRecord.PromptMessages`
  and survives the call — pooling it would break tracing. A real
  reduction here needs either (a) a stack-allocated formatter that
  emits to a writer (API change to `IAdapter.Format`), or (b) lazy
  rendering — both out of scope for an alloc-only dispatch.
- **`HookContext._metadata = new ConcurrentDictionary<…>()` (~150 B per
  call when hooks present):** all 4 `hookCtx.Set(...)` calls in
  Predict.cs land on this dict. Replacing it with a struct-keyed
  array or a `Dictionary` (lossy: not thread-safe, but pipeline is
  serial today) is a public-API risk for hook authors.
- **`new HookPipeline(hookList)` per call (~80 B):** an instance per
  Forward holds the sorted hook array. Caching a per-`AmpelumContext`
  built pipeline is straightforward but touches `WithHook` semantics
  and was scoped out.
- **`HookContext` itself (~80 B):** required by the public `IHook`
  contract per call.
- **Per-hook `hookCtx.Set` writes** allocate boxed value-type entries
  (`long` token counts, `double` durations) — fixable with a typed
  metadata facade, again out of scope here.

The doc `docs/performance/comparative-results.md` is updated with the
new numbers and an explicit "v1.0-rc1 P2 candidates" section listing
the residual sites in priority order.

### Verified

- All 4708 tests in `tests/Ampelum.Tests` pass (Release).
- All 42 tests in `tests/Ampelum.Testing.Tests` pass.
- No public API change. `Ampelum.dll` builds with
  `TreatWarningsAsErrors=true` and zero warnings.

## [0.9.1-alpha.6] — 2026-04-26 — v0.9.1 FINAL: scenarios S10/S11/S13/S14/S15 + S5/S6 follow-ups + ranker + GH workflows

**v0.9.1 milestone COMPLETE.** Closes the verification gate started in v0.9h:
19/19 Pareto integration tests (alpha.1-3) + 11 BDN scenario classes
(alpha.4-6, counting S05b/S06b follow-ups) + ranker tool + 2 GH workflows
+ honest results doc.

### Added — final 5 comparative scenarios + 2 follow-ups

- **`Scenarios/Scenario10_MultiProviderRouting.cs`** — user-brief **S10**
  (must-be-#1 first/only). `RoutedChatClient` + `LoadBalanced` round-robin
  vs hand-rolled cursor loop. **HONEST FINDING:** hand-rolled is ~8×
  faster on the per-call path; `RoutedChatClient`'s value-prop is
  failover + observability, not raw throughput. Documented in
  `docs/performance/comparative-results.md`.
- **`Scenarios/Scenario11_SemanticCacheHit.cs`** — user-brief **S11**
  (must-be-#1 first/only). `SemanticCacheHook` pre-warmed via one
  miss-then-hit cycle vs no-cache baseline. **HONEST FINDING:** with a
  zero-cost mock LM, the cache adds ~17% pure overhead — it inverts
  under any real-LM latency. LangChain.NET memory cache row deferred
  (mock-incompatible adapter).
- **`Scenarios/Scenario13_HookPipelineDepth.cs`** — user-brief **S13**.
  Params={1,4,8,16}. Sub-linear scaling: 16× hook depth → 1.39× cost.
  Order interleaves +/- so the pipeline must sort, exercising
  ascending(before) + descending(after) symmetric dispatch.
- **`Scenarios/Scenario14_AgentLoopTurnLatency.cs`** — user-brief **S14**.
  `GroupChat` over scripted `IAgent` impls vs hand-rolled two-agent
  loop. Params={2,8} turns. Ampelum **BEATS** hand-rolled at 8 turns
  (4.96µs vs 5.70µs) — orchestration overhead amortizes once you exceed
  2-3 turns. AutoGen.NET row deferred (0.2.3 actor-bus model is
  mock-incompatible without measuring plumbing).
- **`Scenarios/Scenario15_FormatAdapterConversion.cs`** — user-brief
  **S15** (must-be-#1 first/only). Full triangular round-trip
  OAI→Anthropic→Gemini→OAI plus per-direction baselines. No competitor
  ships an equivalent provider-neutral converter API.
- **`Scenarios/Scenario05b_TwoCallToolRoundTrip.cs`** — user-brief
  **S5 follow-up**. Removes the alpha.5 dispatch-isolation cut by
  scripting a deterministic two-call sequence on `FakeChatClient`:
  response 1 = `FunctionCallContent("add", a=2, b=3)`; response 2 =
  `final_answer: 5`. Driven through `ReAct<QaSig>` end-to-end. Three
  rows: Ampelum_ReAct, Sk_TwoCallEquivalent (hand-written MEAI two-call
  dispatch — the same primitive SK's `ChatCompletionAgent` ultimately
  calls), RawTwoCalls.
- **`Scenarios/Scenario06b_BatchedAuditAmortized.cs`** — user-brief
  **S6 follow-up**. `BatchedAuditSink` amortized companion. Params
  Batch={32,256}. **HONEST FINDING:** with `InMemoryAuditSink` as the
  inner sink (no I/O), batching is *slower* than synchronous append
  because the channel write + drain task overhead is pure tax. The
  amortization win materialises only against a sink with real I/O
  latency. Per the alpha.6 honest-reporting brief, both per-call AND
  amortized numbers are reported with the tradeoff documented.

### Added — ranker + workflows + honest results doc

- **`tools/comparative-rank.csx`** — dotnet-script that parses BDN
  per-scenario CSV reports, computes ~95% CI from BDN's 99.9% Error
  half-width (`× 1.96/3.291`), and evaluates 6 must-be-#1 rules: M1
  orchestration vs raw MEAI; M2 alloc per Forward; M3 AOT cold-start
  p50 ≤30ms (reads `tools/cold-start-result.json`); M4 optimizer
  compile ≤50% DSPy-py; M5 audit chain throughput per-call AND
  amortized; M6 multi-tenant scaling N=64/N=1 ≤1.5×. Exits non-zero
  on any FAIL.
  **Sample run (smoke, 1 iteration):** **6 of 6 must-be-#1 metrics FAIL.**
  See `docs/performance/comparative-results.md` for the why.
- **`.github/workflows/comparative-nightly.yml`** — nightly cron at
  04:00 UTC + `comparative-required` PR label + manual dispatch. Builds
  ComparativeBenchmarks Release, publishes Ampelum.AotSmoke for
  linux-x64, runs `tools/measure-cold-start.sh 10`, runs the full BDN
  suite (warm-up=3, iterations=5), runs the ranker, archives BDN
  artifacts.
- **`.github/workflows/pareto-integration.yml`** — nightly + `integration-required`
  PR label + manual dispatch. Consumes `AMPELUM_INTEGRATION_OPENROUTER_KEY`
  from secrets; publishes AotSmoke for `AMPELUM_AOT_PROBE_PATH`; runs
  the 19-test Pareto suite. Without the secret the suite Skip-passes.
- **`docs/performance/comparative-results.md`** — honest reporting
  record. Documents WHY each must-be-#1 currently fails on the smoke
  run, what v1.0-rc1 must address, and why ratios are pessimistic
  against a free mock LM. The point is to know where we stand before
  v1.0, not to fudge.

### v0.9.1 final tally

- **Pareto integration:** 19/19 tests across alpha.1-3
- **Comparative benchmarks:** 11 scenario classes (S1, S2, S3, S4, S5,
  S05b, S6, S06b, S9, S10, S11, S12, S13, S14, S15) — that's 15 named
  scenarios counting follow-ups; S7 is a driver-only marker, S8 is a
  Python-subprocess scenario.
- **Tooling:** ranker CSX + 2 GH workflows + cold-start driver.
- **v1.0-rc1 carry-forward gaps:** 9 documented (RetryHook auto-loop,
  Predict parameterless ctor, Predict.StreamAsync no-audit, GroupChat
  audit kind, AuditEntry.EventType, BatchedAuditSink amortization
  tradeoff, M1 18× orchestration overhead, M2 4.7KB alloc vs 512B
  budget, M6 20× N=64 p99 inflation). See master plan §6.H alpha.6
  status.

### Changed

- `tests/Ampelum.ComparativeBenchmarks/Ampelum.ComparativeBenchmarks.csproj`:
  added `<NoWarn>` entry for `AMPELUM_V09_PREVIEW` so v0.9-experimental
  surfaces compile in the bench project; added `<ProjectReference>` on
  `Ampelum.Testing` for `FakeChatClient` (S05b).

---

## [0.9.1-alpha.5] — 2026-04-26 — v0.9.1 batch 5: comparative benchmark scenarios S4–S9 + Python subprocess runner

Builds on the alpha.4 skeleton. Six new scenarios land plus the Python
reference subprocess harness body, `python-ref/` scaffolding, and the
S7 AOT cold-start outer driver script. Five scenarios (S10/S11/S13/S14/S15)
and the alpha.6 ranker tool + GH workflows remain deferred per the v0.9h
master plan.

### Added — comparative benchmark scenarios

- **`Scenarios/Scenario04_StreamingThroughput.cs`** — user-brief **S4**.
  `[MemoryDiagnoser]`, `[Params(100)]` chunks/stream. Compares
  `Predict.StreamAsync` vs raw MEAI streaming vs SK chat-streaming through
  the new `ChunkedMockChatClient`. AutoGen.NET / LangChain.NET omitted
  (no documented `IChatClient`-streaming hook in the pinned versions —
  alpha.6 revisits). Budget D7: ≥100k chunks/sec parse path.
- **`Scenarios/Scenario05_ToolCallingRoundTrip.cs`** — user-brief **S5**.
  Scope-reduced to dispatch-isolation: bench Ampelum's typed-tool
  `InvokeAsync` JSON-args path vs SK `KernelFunction.FromMethod`
  `InvokeAsync` vs raw delegate. Full LM-driven two-call round-trip
  deferred to alpha.6 (advisor flagged the SK
  `AutoInvokeKernelFunctions` mock-plumbing as fragile). Budget B3:
  ≤5% behind SK.
- **`Scenarios/Scenario06_AuditChainThroughput.cs`** — user-brief **S6**.
  `[MemoryDiagnoser]`. Ampelum's `AuditHook` + `InMemoryAuditSink`
  end-to-end through `Predict.ForwardAsync` vs a naive hand-rolled
  `JsonSerializer.SerializeToUtf8Bytes` + `SHA256.HashData` chain.
  Honesty note in xmldoc: per-entry compute is strictly heavier in
  Ampelum (canonical sort + IncrementalHash + BigEndian framing); the
  must-be-#1 story rests on `BatchedAuditSink` amortisation, deferred
  to alpha.6.
- **`Scenarios/Scenario07_AotColdStart.cs`** — user-brief **S7**.
  Marker class + driver script (`tools/measure-cold-start.sh`). BDN's
  warm-up loop defeats cold-start measurement; alpha.5 ships an outer
  shell harness that times the existing `Ampelum.AotSmoke` AOT-published
  binary across N spawns and emits one JSON line with min/p50/p95/max.
  Budget D4: ≤30 ms p50 on linux-x64 (authoritative platform).
- **`Scenarios/Scenario08_OptimizerCompile.cs`** — user-brief **S8**.
  `[MemoryDiagnoser]`, `[Params(50)]` examples. Ampelum
  `BootstrapFewShot.CompileAsync` vs Python reference via
  `PythonDspyRunner`. Python row short-circuits to a sentinel result
  when Python is absent (BDN has no `[Skip]`). Budget D6: ≤50% of
  Python wall-clock.
- **`Scenarios/Scenario09_NParallelScaling.cs`** — user-brief **S9**.
  `[MemoryDiagnoser]`, `[Params(1, 8, 32, 64)]`. Concurrent
  `Predict.ForwardAsync` calls against a shared `MockChatClient` with
  `Task.WhenAll`. Budget D5: linear to `Environment.ProcessorCount`,
  p99 ≤ 1.5× baseline at N=64 (p99 enforcement deferred to the alpha.6
  ranker which reads BDN per-iteration measurements CSV).

### Added — Python subprocess harness

- **`SubprocessHarness/PythonDspyRunner.cs`** — full `RunAsync` body.
  `Process.Start` + JSON-line wire protocol on stdin/stdout. Probes
  `uv`/`python`/`python3` on PATH (or `AMPELUM_COMPARATIVE_PY`
  override) with cached `IsAvailable` / `Launcher` lazies. Resolves
  `python-ref/` by walking up from `AppContext.BaseDirectory` so the
  binary works whether run from the repo root or its bin output.
  `RunResult` flipped from `readonly record struct` to `sealed record`
  to match the snake_case JSON deserialisation path.
- **`python-ref/pyproject.toml`** — uv-managed project, Python 3.12+,
  pins `dspy-ai==2.5.43`, `pydantic>=2.7,<3`, `tiktoken>=0.7,<1`,
  `psutil>=5.9,<7`. `[tool.uv] package = false`.
- **`python-ref/requirements.txt`** — same pins for the bare-`pip`
  fallback path.
- **`python-ref/runner.py`** — single-line JSON request/reply loop with
  `_SCENARIOS` dispatch table. Implements `S8_bootstrap_fewshot_compile`
  against a deterministic cycling-mock LM. Catches all exceptions and
  surfaces them via the JSON `output` field so the C# side gets a
  deterministic stdout line.
- **`python-ref/README.md`** — uv + pip setup, wire protocol table,
  CI authority note.

### Added — competitor adapters

- **`Competitors/Sk/SkStreaming.cs`** — SK chat-streaming row for S4
  (alpha.6 wires real `Kernel` + `ChatHistory` streaming).
- **`Competitors/Shared/ChunkedMockChatClient.cs`** — streaming-focused
  mock that splits a canned response into N equal chunks; single
  `Task.Yield` up front mirrors a hot TCP-pooled provider.

### Added — outer driver script

- **`tools/measure-cold-start.sh`** — bash driver for S7. Times the
  AOT-published `Ampelum.AotSmoke` binary across `iterations` spawns,
  emits one JSON line: `{"iterations":N,"min_ms":...,"p50_ms":...,
  "p95_ms":...,"max_ms":...}`. Detects linux-x64 and win-x64 publish
  outputs; falls back to a clear setup error if neither exists.

### Notes / honest gaps

- **S4 streaming** — AutoGen.NET 0.2.3 and LangChain.NET 0.17.0 do not
  expose a clean streaming-iteration hook against an arbitrary
  `IChatClient` mock. Both rows omitted; alpha.6 revisits if a wrapping
  harness lands.
- **S5 tool-calling** — alpha.5 ships dispatch-isolation. Full LM-driven
  two-call round-trip (mock that switches canned reply on the second
  call + SK `AutoInvokeKernelFunctions`) deferred to alpha.6.
- **S6 audit** — Ampelum's per-entry hash compute is strictly heavier
  than naive `JsonSerializer.SerializeToUtf8Bytes` + `SHA256.HashData`;
  the must-be-#1 story relies on `BatchedAuditSink` amortisation under
  steady-state throughput, which surfaces once the alpha.6 ranker tool
  can read multi-row throughput summaries.
- **S7 AOT cold-start** — driver-only ship; CI workflow integration
  + p50 gate enforcement land in alpha.6.

### Deferred to alpha.6 (final)

- Scenarios **S10** (RAG retrieval round-trip), **S11** (MIPROv2
  compile), **S13** (streaming-cancel propagation), **S14** (save/load),
  **S15** (dataset materialisation).
- `tools/comparative-rank.csx` — 95% CI + must-be-#1 ranker.
- `.github/workflows/comparative-nightly.yml` +
  `.github/workflows/pareto-integration.yml`.
- Idiomatic SK `Kernel.InvokeAsync` + AutoGen agent harness + LangChain
  chain wiring across all scenarios (alpha.5 keeps the chat-routed
  competitor adapters from alpha.4 except where marked).

### Quality bar

- Full-solution `dotnet build -c Release` clean: 0 warnings, 0 errors.
- `tests/Ampelum.ComparativeBenchmarks` build clean against the
  isolated CPM (`Directory.Packages.props`) — no perturbation of the
  root SUT package graph.
- `TreatWarningsAsErrors=true` preserved; `<NoWarn>` scope unchanged.
- No `dspy`/upstream-framework strings in main C# source — exception
  contained to `tests/Ampelum.ComparativeBenchmarks/python-ref/` per
  the v1.0 rebrand spec (alpha.6 grep gate allowlists that folder).

## [0.9.1-alpha.4] — 2026-04-26 — v0.9.1 batch 4: comparative benchmarks skeleton + 3 must-be-#1 scenarios

Stands up the comparative-benchmarks project. New
`tests/Ampelum.ComparativeBenchmarks` BenchmarkDotNet console app, NOT a
test project, NOT coverage-gated, with an isolated CPM
(`Directory.Packages.props`) that shadows the root pin set so competitor
transitive closures cannot perturb the SUT package graph. Wired into the
root `Ampelum.sln`; full-solution `dotnet build -c Release` is clean
(0 warnings, 0 errors).

### Added — comparative benchmark project skeleton

- **`tests/Ampelum.ComparativeBenchmarks/Ampelum.ComparativeBenchmarks.csproj`** —
  `OutputType=Exe`, `IsTestProject=false`, `IsPackable=false`,
  `Optimize=true`. `<NoWarn>` scoped tightly to known competitor-pin
  warnings (NU1701, NU1603, NU1605, SKEXP0001, SKEXP0010, CS1591) with
  documenting comments inline. `TreatWarningsAsErrors=true` from the
  root `Directory.Build.props` is preserved.
- **`tests/Ampelum.ComparativeBenchmarks/Directory.Packages.props`** —
  isolated CPM. Shared deps (MEAI 10.5.0, OpenAI 2.10.0, BenchmarkDotNet
  0.14.0) match the root pin set so the SUT references resolve to the
  same DLLs as the production library. Competitor pins land here.
- **`Program.cs`** — single-line `BenchmarkSwitcher.FromAssembly(...).Run(args)`
  matching `Ampelum.Benchmarks`.

### Added — competitor pins (3 of 4 in scope this milestone)

- **Microsoft.SemanticKernel** 1.74.0 (latest stable, verified via
  `dotnet package search`).
- **AutoGen** 0.2.3 (Microsoft AutoGen.NET meta-package).
- **LangChain** 0.17.0 (tryAGI/LangChain.NET port).
- **Microsoft.Extensions.AI** 10.5.0 + **OpenAI** 2.10.0 — pinned in the
  isolated CPM at the same versions as the root so the SUT and competitor
  rows share the same MEAI types.
- **Python DSPy reference** — `SubprocessHarness/PythonDspyRunner.cs`
  ships as a contract-only skeleton with `RunAsync` throwing
  `NotImplementedException`; the real `Process.Start` body, uv-managed
  venv, `pyproject.toml`, `requirements.txt`, and JSON wire protocol
  land in v0.9.1-alpha.5 per the v0.9h master plan §6.H.

### Added — 3 must-be-#1 scenarios (alpha.4 highest-priority slice)

User-brief labels (S1/S2/S3) map to plan-file numbering so alpha.5
plug-ins of the remaining 12 scenarios do not need a renumber:

- **`Scenarios/Scenario01_SinglePredict.cs`** — user-brief **S1**.
  `[MemoryDiagnoser]`. Single Predict no-hooks orchestration overhead.
  Compares Ampelum (baseline) vs MEAI raw vs SK vs AutoGen.NET vs
  LangChain.NET against a byte-identical `MockChatClient`. Budget D1:
  Ampelum p50 ≤ MEAI raw + 500 ns and ≤ +256 B alloc.
- **`Scenarios/Scenario02_PredictPlus4HookPipeline.cs`** — user-brief **S3**.
  `[MemoryDiagnoser]`. Predict + 4 noop `IHook` impls (alpha.5 swaps in
  real budget+audit+retry+log). SK competitor uses inline 4-stage
  delegate chain (alpha.5 wires the real `Kernel`-filter pipeline).
  Budget D2: 4-hook overhead ≤ 10 µs.
- **`Scenarios/Scenario12_AllocationPerPredict.cs`** — user-brief **S2**.
  `[MemoryDiagnoser]`. Allocation per Forward; reads the BDN
  `Allocated` column. Budget D3: ≤ 512 B no-hooks.

### Added — competitor placeholders (alpha.4 keeps the wire shape stable)

- **`Competitors/Sk/SkSinglePredict.cs`** + `SkPipeline.cs` — minimal SK
  routes through the same `IChatClient` mock so LM cost is held constant.
  alpha.5 swaps for `Kernel.InvokeAsync` + `KernelFunction.FromPrompt`.
- **`Competitors/AutoGenNet/AutoGenSinglePredict.cs`** — placeholder;
  alpha.5 wires the real `Microsoft.AutoGen.Core` agent harness.
- **`Competitors/LangChainNet/LangChainSinglePredict.cs`** — placeholder;
  alpha.5 wires `chain.RunAsync`.
- **`Competitors/Shared/MockChatClient.cs`** + `QaSig.cs` — single shared
  mock LM and signature so every competitor measures the same prompt
  shape and orchestration delta.

### Added — README

- **`tests/Ampelum.ComparativeBenchmarks/README.md`** — how to run, how
  to interpret BDN output, the user-brief-to-plan-file scenario number
  mapping, the must-be-#1 budget rules per scenario, the Python-setup
  deferred section, and the rationale for isolated CPM.

### Project naming note

The csproj is named `Ampelum.ComparativeBenchmarks` (per the alpha.4
brief), not `Ampelum.Benchmarks.Comparative` (per the v0.9h plan). This
is intentional and surfaced in the README user-brief mapping table; the
folder-level layout otherwise matches the plan.

### Deferred to v0.9.1-alpha.5 / alpha.6 (final)

- Scenarios S4-S15 (12 remaining: streaming, ChainOfThought, ReAct,
  RAG, BootstrapFewShot, MIPROv2, save/load, N-parallel, AOT,
  alloc-pipeline, audit-throughput, budget-zero-cost, tool-call).
- `PythonDspyRunner.RunAsync` body + uv venv + Python reference script
  + `pyproject.toml` + `requirements.txt`.
- `tools/comparative-rank.csx` — 95% CI + must-be-#1 ranker.
- `.github/workflows/comparative-nightly.yml` and
  `.github/workflows/pareto-integration.yml`.
- Idiomatic SK `Kernel.InvokeAsync` / AutoGen agent-harness / LangChain
  `chain.RunAsync` wiring (alpha.4 placeholders ship to keep the table
  layout stable).

### Verification

- `dotnet build tests/Ampelum.ComparativeBenchmarks -c Release` — 0
  warnings, 0 errors.
- `dotnet build Ampelum.sln -c Release` — full solution clean: 0
  warnings, 0 errors. Existing 4708 unit + 26 integration tests
  unaffected (no production code touched this milestone).

---

## [0.9.1-alpha.3] — 2026-04-26 — v0.9.1 batch 3: final 4 Pareto tests (19 of 19 — COMPLETE)

Closes Pareto integration coverage. Adds the four remaining scenarios
(#15, #17, #18, #19) from master plan §6.H. With this release the 19
Pareto contracts from master spec §9 are 100% wired; only the 15
comparative benchmarks + competitor pins remain for `v0.9.1-alpha.4`
(final).

### Added — Pareto integration tests (4 of 19, closes the suite)

- **#15 — Provider format adapter round-trip**: `ToolCallFormatConverter`
  round-trips a realistic tool-call payload (parallel calls + nested
  arguments + arrays + scalars) OpenAI → Anthropic → OpenAI without
  losing data. Ids, names, and the full argument bag survive equality.
  Empty argument bags also round-trip (regression for Anthropic
  missing-`input` shape). Pure JSON, no fixture, no real LM, runs
  unconditionally. **NOTE — scope swap:** this replaces the original
  v0.9h plan #15 ("Optimizer mid-trial failure"), which is deferred to
  a v1.0 follow-up; the renumbering is intentional and surfaced in
  master plan §6.H.
- **#17 — OpenTelemetry trace emission**: a single Forward against a
  `FakeChatClient` emits exactly one `Activity` on the `Ampelum`
  `ActivitySource` with the gen_ai.* OTel semconv 1.27 tags
  (`gen_ai.system`, `gen_ai.operation.name=chat`,
  `gen_ai.semconv.version`, `gen_ai.request.model`), the ampelum.*
  span tags (`ampelum.module`, `ampelum.signature_type`,
  `ampelum.adapter`, `ampelum.demos.count`, `ampelum.trace_id`), and
  the expected phase events (`ampelum.adapter.format`,
  `ampelum.adapter.parse`, plus `ampelum.hook.before`/`after` when a
  hook is attached). Filters by `ampelum.trace_id` so it stays robust
  under the xUnit.v3 parallel runner.
- **#18 — Multi-agent group chat**: `GroupChat` over 2 hand-rolled
  `IAgent` impls + `RoundRobinSelector` + `InMemoryConversationMemory`.
  Asserts (a) round-robin selection drives the order on clean turns,
  (b) `AgentMessage.Handoff` overrides the selector on the next turn,
  (c) `AgentMessage.IsTerminal` terminates the chat, (d) shared
  conversation memory accumulates one entry per appended message and
  recall via `GetRecentAsync` returns the full ordered history.
  Deterministic scripted agents keep it offline.
- **#19 — AOT smoke through Predict**: extends
  `tests/Ampelum.AotSmoke/Program.cs` with a process-local
  `ActivityListener` and a stable parse-friendly stdout contract
  (`Predict completed: <answer>` + `ampelum.trace.id=<hex>` +
  `aot-smoke ok`). The new `Pareto19_AotBinaryRealPredict` test execs
  `$AMPELUM_AOT_PROBE_PATH` and asserts the contract holds plus that
  no `RequiresDynamicCode`/`RequiresUnreferencedCode` warnings leak to
  stderr at runtime. Skips cleanly when the env var is unset (default
  local dev / Windows). On the existing `aot-smoke` CI job (linux-x64),
  the published binary now also serves as the integration probe — no
  new sample project required.

### Product gaps surfaced (v1.0 blockers)

- **`AuditMetadataKeys` has no `agent.handoff` event-type** (Pareto #18).
  The `AuditHook` is wired to `Predict.ForwardAsync` only; multi-agent
  handoffs are observable today via `GroupChatResult.History` and
  optional `IConversationMemory`, but not as audit-chain rows. v1.0
  gap; the test asserts the observable surface as it exists today.
- **Hook-phase span observation is via `ActivityEvent`, not span tags**
  (Pareto #17). The user task spec asked for "ampelum.* span attributes
  for hook phases"; today's product emits `ampelum.hook.before` /
  `ampelum.hook.after` events on the parent span, not tags. Equally
  observable for downstream OTel exporters but worth surfacing to the
  spec authors. The test asserts on events.

### Tests

- **+4 integration tests** (`Ampelum.IntegrationTests`); now 17 active
  + 9 real-LM gated (cleanly skip without `AMPELUM_INTEGRATION_OPENROUTER_KEY`).
  Unit-test count unchanged at 4708, 100% line coverage maintained.

## [0.9.1-alpha.2] — 2026-04-26 — v0.9.1 batch 2: 7 new Pareto tests (15 of 19)

Second v0.9.1 milestone batch. Adds the persistence, POLC, hook-pipeline,
streaming-finalize, evaluate, parse-recovery, and audit-continuity Pareto
tests. Six of the seven do not require `AMPELUM_INTEGRATION_OPENROUTER_KEY`
(deterministic `FakeChatClient` fixtures); only Pareto #9's contract is
also exercised against the real-LM in #5 (alpha.1).

### Added — Pareto integration tests (7 of 19)

- **#6 — Persistence round-trip**: SaveAsync → LoadAsync over a `QaProgram`
  shim (parameterless ctor + `WithContext`); demos and instruction
  overrides preserved; bound program produces the same `Answer` as the
  pre-save baseline against an identical scripted fake. Documents the
  alpha.1 #4 product gap (Predict has no parameterless ctor) by routing
  around it via the test-only shim.
- **#7 — POLC tool capability**: `ToolGateHook` + `ToolGatePolicy(DenyCategories
  = {DataMutation})` admits a `DataAccess` tool and throws
  `ForbiddenToolException` on a `DataMutation` tool both as a unit and
  end-to-end through `Predict.ForwardAsync` (LM never called when denied).
  Compile-time half cross-referenced to `tests/Ampelum.Analyzers.Tests/Amp002Tests.cs`.
- **#8 — Hook pipeline ordering**: 4-hook stack with non-monotonic Order
  values; OnBefore ascending, OnAfter descending; verifies short-circuit
  via `ctx.Outputs` skips the LM call AND still runs all OnAfter hooks
  in descending order (PSQ-11).
- **#9 — Streaming finalize**: deterministic `FakeChatClient` script;
  partial chunks arrive non-shrinking, terminal update emits final value
  byte-equal to the non-streaming pass over the same input. Re-documents
  the streaming-bypasses-hooks gap from #5.
- **#10 — Evaluate harness**: 5-example labelled dataset, custom
  case-insensitive `IMetric<QaSig>`, `MaxDegreeOfParallelism=2`; aggregates
  Mean=0.6 + scores split 3-correct/2-wrong; concurrency probe asserts
  observed peak in-flight ≤ DOP cap. Race-free under parallelism via a
  custom keyed `IChatClient` (avoids `FakeChatClient` queue-dequeue races).
- **#12 — Adapter parse-error recovery**: malformed response throws
  `AdapterParseException`; `RetryHook.OnErrorAsync` records
  `retry.recovered=true` + `retry.corrective_prompt` in metadata; manual
  follow-up forward with a valid response recovers the canonical answer.
- **#13 — Audit chain hash continuity**: 3 sequential forwards → 3 audit
  entries; per-pair `PrevHash[i] == EntryHash[i-1]` linkage verified
  directly (stronger than `AuditChainVerifier` alone, which is also run
  as an independent oracle); genesis `PrevHash` is 32 zero bytes.

### Product gaps surfaced (v1.0 blockers)

- **`Predict.ForwardAsync` does NOT auto-loop on `retry.corrective_prompt`**
  (Pareto #12). `RetryHook` writes recovery metadata as a passive marker,
  then the parse exception propagates. Auto-recovery (re-prompt with
  corrective text up to `maxRecoveryAttempts`) is unimplemented; v1.0
  blocker. Today's contract is "metadata captured + caller drives the
  retry"; the test asserts that contract and drives the retry manually.
- **`Predict<TSig>` has no public parameterless ctor** so it cannot be
  the `TProgram` of `JsonProgramPersistence.LoadAsync<TProgram>` directly
  (Pareto #6, also alpha.1 #4). Mitigated in tests with a `QaProgram`
  shim. v1.0 candidate: relax the persistence constraint or add a
  `Predict<TSig>.CreateUnbound()` factory.
- **`Predict.StreamAsync` bypasses the hook pipeline** (Pareto #5, #9).
  Audit-chain symmetry between streaming and non-streaming is not
  observable on the public surface. Tracked as the streaming-hooks
  v0.10/v1.0 work item.

### Pending in v0.9.1

- 4 Pareto tests remain: **#11 Budget exhaustion** (alpha.1 stub already
  filled — keep), **#15 Optimizer mid-trial failure**, **#17 Multi-agent
  group chat**, **#18 AOT binary** (rebrand-blocked: AOT smoke is
  separate; the integrated Pareto requires a published probe).
  Note: the prompt's enumerated set (#6/#7/#8/#9/#10/#12/#13) is the
  alpha.2 cut and reflects renumbering vs the original v0.9h plan.
- 15 comparative benchmarks (master plan §6.H S1–S15) retargeted to
  v0.9.1-alpha.3+.

## [0.9.1-alpha.1] — 2026-04-26 — v0.9.1 batch 1: 3 Pareto stubs filled + 5 new Pareto tests (8 of 19)

First v0.9.1 milestone batch. Closes the runtime-skip stubs introduced in
alpha.12 (#11/#14/#16) with real-LM bodies and adds the simple-Predict
family (#1/#2/#5), ReAct (#3), and BootstrapFewShot (#4). Tests are gated
on `AMPELUM_INTEGRATION_OPENROUTER_KEY` and skip cleanly when the env var
is unset; they run as `[Trait("Cost", "Cheap")]` for selector filtering.

### Added — Pareto integration tests (8 of 19)

- **#1 — Predict happy path**: real Predict + AuditHook, single audit
  entry, chain valid via `AuditChainVerifier`.
- **#2 — ChainOfThought**: real CoT forward, rationale and answer both
  populated, audit chain valid.
- **#3 — ReAct + 2 tools**: `lookup_capital` + `uppercase` registered as
  `AIFunction`s; trace's `TraceKeys.ToolCalls` list non-empty; at least
  one tool invocation observed end-to-end.
- **#4 — BootstrapFewShot**: 3-example trainset compile produces ≥1
  bootstrapped demo, bound forward succeeds on an unseen prompt,
  `JsonProgramPersistence.SaveAsync` round-trips to a non-empty file.
- **#5 — Streaming**: chunks arrive in order with monotonically growing
  partial text; terminal `IsFinal` update yields a typed `Answer`
  matching the non-streaming reference value (audit-hash equality
  deferred — `Predict.StreamAsync` bypasses the hook pipeline).

### Filled — alpha.12 stubs (3 of 3)

- **#11 — Budget exhaustion under parallel forwards**: N=4 parallel calls
  with `CallLimit=2`; documents the OnAfter/descending semantics
  (`AuditHook` records ALL N entries before `BudgetHook.Consume` rejects
  N−K). `BudgetExceededException` count matches expected; chain valid.
- **#14 — Provider chaos**: `RoutedChatClient` wires 2 fakes that always
  throw `HttpRequestException` ahead of one healthy real-LM endpoint;
  `Failover` walks the chain and the call completes. Mid-call kill
  harness deferred (still requires controllable HttpMessageHandler).
- **#16 — Cache concurrency**: warm-up + 7 parallel hits on a single
  `SemanticCacheHook` + `InMemorySemanticCache`; a `CountingChatClient`
  decorator proves the inner LM fires exactly once; cache size stays
  bounded at 1; audit chain valid over all 8 entries.

### Scope-reductions documented in test XML docs

- Audit-row "EventType" assertions (`budget.exhausted`, `router.failover`)
  could not be made: `AuditEntry` has no `EventType` field today, so the
  named constants in `AuditMetadataKeys` are aspirational pending a
  row-shape extension. Tests assert the observable contract — chain
  validity over the entries that did land — and document the gap.
- BootstrapFewShot "outperforms baseline on test split" reduced to
  "compile succeeds + bound forward succeeds + persistence round-trip"
  to fit the per-test 5-call cost cap.
- Streaming "audit hash equality with non-streaming" reduced to "final
  value equality"; streaming intentionally bypasses the hook pipeline.

### Infrastructure

- `tests/Ampelum.IntegrationTests/Fixtures/OpenRouterClientFactory.cs`:
  shared real-LM client construction (OpenAI SDK 2.10.x + MEAI 10.5.x,
  endpoint `https://openrouter.ai/api/v1`, default model
  `openai/gpt-oss-20b:nitro`).
- Project gains `OpenAI`, `Microsoft.Extensions.AI.OpenAI`,
  `Microsoft.Extensions.AI`, `Microsoft.Extensions.Logging.Abstractions`
  package references; coverage is still excluded
  (`<ExcludeFromCodeCoverage>true</ExcludeFromCodeCoverage>` in csproj).

### Pending in v0.9.1

- 11 Pareto tests remain (#6 Evaluate, #7 BootstrapFewShot comparative-
  rank shape, #8 MIPROv2, #9 Persistence, #10 Hook pipeline Budget+Audit
  composition, #12 Audit tamper detection, #13 Audit replay, #15
  Optimizer mid-trial failure, #17 POLC forbidden tool, #18 Multi-agent
  group chat, #19 AOT binary). Retargeted to v0.9.1-alpha.2 / .3.
- 15 comparative benchmarks (master plan §6.H S1–S15) retargeted to
  v0.9.1-alpha.2 / .3.

---

## [0.9.0-alpha.12] — 2026-04-26 — v0.9g batch submitters + v0.9h skeleton + 3 Pareto stubs (batch 3 of 3)

Closes v0.9g and opens v0.9h. This batch ships the provider batch API
primitive plus two file-based async submitters (the 50% cost lever for
high-volume workloads), scaffolds the dedicated `Ampelum.IntegrationTests`
project, and lands the first three Pareto integration scenarios as runtime-
skip stubs gated on `AMPELUM_INTEGRATION_OPENROUTER_KEY`.

### Added — v0.9g (Tasks 7/8 — batch submitters)

- `Ampelum.Batch` namespace under `[AMPELUM_V09_PREVIEW]`:
  - `IBatchChatClient` interface (PSQ-4): `SubmitAsync` / `GetStatusAsync` /
    `StreamResultsAsync` / `CancelAsync`.
  - `BatchRequest` (CustomId + Messages + per-item Options),
    `BatchHandle` (Id + Provider + SubmittedAt), `BatchStatus`
    (State + counts), `BatchResultItem` (CustomId + Response | ErrorMessage),
    `BatchState` enum (InProgress / Completed / Failed / Cancelled / Expired).
  - `OpenAIBatchSubmitter`: multipart file upload to `/v1/files`, batch
    create at `/v1/batches`, status poll, jsonl result parser with
    success-and-per-item-error branches; preserves preset `BaseAddress` +
    `Authorization` header for OpenRouter / Azure use.
  - `AnthropicBatchSubmitter`: inline-jsonl submit to
    `/v1/messages/batches`, results parser handles multi-block `content[]`
    text-only filtering, status mapping for ended/canceling/canceled.
- 69 unit tests with mocked `HttpMessageHandler`, 100% line coverage on
  `Ampelum.Batch.*`. Tests cover every state-string branch, every
  result-shape error variant, all null-arg validation, and constructor
  preservation of preset HTTP client state.

### Added — v0.9h (Task 11 IntegrationTests skeleton + 3 of 19 Pareto)

- `tests/Ampelum.IntegrationTests` project (NOT coverage-gated; xUnit.v3,
  TWE=true, ExcludeFromCodeCoverage):
  - `PerfBudgets.cs` — D1–D7 + per-test budgets verbatim from spec §D.
  - `Skips/SkipPredicates.cs` — `SkipUnlessRealLm` (env var
    `AMPELUM_INTEGRATION_OPENROUTER_KEY`, runtime-evaluated for xUnit.v3
    `Assert.SkipWhen`), `SkipUnlessLinux`, `SkipUnlessAotPublished`.
  - `Fixtures/ParetoTestFixture.cs` — shared `FakeChatClient` +
    `InMemoryAuditSink` + documented 5-cent per-test cost cap (callers
    self-enforce when wiring real-LM).
  - `Pareto/Pareto00_FixtureSmoke.cs` — always-on smoke proving wire-up.
- Pareto integration tests #11 / #14 / #16 (master plan §6.H):
  - **#11 Budget exhaustion** — N=64 parallel, K=20 cap, exact-zero
    overshoot, single `budget.exhausted` audit entry.
  - **#14 Provider chaos** — `RoutedChatClient` + `FailoverStrategy`
    primary-killed-mid-call → secondary takes over, audit chain intact
    with `router.failover` from/to entry.
  - **#16 Cache concurrency** — 100 parallel identical-input forwards
    on cold cache: exactly 1 inner LM call, 99 hits, no torn state.
- All three ship as runtime-skip stubs (skip-when-unset by default;
  emit explicit `Assert.Skip("v0.9.1: real-LM body pending")` when the
  key IS set so the TODO surface stays loud).

### Status — §6.G COMPLETE; §6.H PARTIAL (3 of 19)

- v0.9g audit rows fully shipped: 6.3 (multi-provider routing/failover),
  6.4 (load balancing across keys), 6.6 (semantic cache), 6.9 (rate
  limit), 6.10 (circuit breaker), 12.3 (batch API), 12.4
  (function-call format adapter), 12.5 (tool-call format conversion).
- 3 of 19 Pareto tests landed. **The remaining 16 Pareto tests, the 15-
  scenario comparative bench suite (6 competitor pins, 2 Python
  harnesses, ranking parser, 2 GH workflows) are RETARGETED to a
  dedicated v0.9.1 milestone.** v0.9.0 ships v0.9a-v0.9g shipped +
  v0.9h skeleton + 3-Pareto smoke; v0.9.1 closes the verification gate.

Master plan §6.G + §6.H patched in this commit set.

## [0.9.0-alpha.11] — 2026-04-26 — v0.9g rate limit + circuit breaker + format adapters (batch 2 of 3)

Second of three slices that complete v0.9g. This batch ships client-side
rate limiting (PSQ-3), Polly v8 circuit-breaker integration, and the
cross-provider tool-call format converter that unblocks the v0.9h
comparative benchmark scenario #15. Test count rises from 4578 to 4646
(+68); coverage on the new files is 100% line.

### Added — Rate limiting (PSQ-3)

- `Ampelum.RateLimiting.RateLimitingOptions` (sealed record) — sustained
  RPM, burst multiplier (default 5×), acquire timeout (default 30 s).
- `Ampelum.RateLimiting.RateLimitedChatClient : IChatClient` decorator
  built on `System.Threading.RateLimiting.PartitionedRateLimiter<string>`.
  Per-partition token buckets keyed off
  `ChatOptions.AdditionalProperties[HookMetadataKeys.BudgetScope]`,
  falling back to `RouterTargetName`, then to `"default"`.
- `Ampelum.Exceptions.RateLimitedException` (derives `AmpelumException`)
  with `PartitionKey` and `WaitTime` properties.
- DI extension `AmpelumBuilder.AddRateLimit(options?)` wraps the
  registered `IChatClient` (instance / factory / type form).

### Added — Circuit breaker

- `Ampelum.Resilience.CircuitBreakerOptions` (sealed record) — failure
  threshold (default 5), break duration (default 30 s, defensively
  clamped to Polly's [500 ms, 24 h] window), and a customisable
  `ShouldTrip` predicate (default trips on `HttpRequestException` plus
  any exception whose type name contains `RateLimit`).
- `Ampelum.Resilience.CircuitBreakerChatClient : IChatClient` fronts the
  inner client with a Polly v8 `ResiliencePipeline`. Streaming initiates
  through the breaker so an Open state surfaces before the first chunk
  is yielded.
- DI extension `AmpelumBuilder.AddCircuitBreaker(options?)` mirrors the
  rate-limit wiring shape.

### Added — Provider format adapters (PSQ-7)

- `Ampelum.ProviderAdapters.NormalizedToolCall` sealed record carrying
  `Id`, `Name`, and a JSON-shaped argument bag.
- `Ampelum.ProviderAdapters.ToolCallFormatConverter` — pure converter
  with six static methods: `FromOpenAi` / `ToOpenAi`,
  `FromAnthropic` / `ToAnthropic`, `FromGemini` / `ToGemini`. Round-trip
  preserves argument shape across all three providers; nested objects,
  arrays, and every JSON scalar kind are honoured. Gemini calls
  synthesise stable `gemini-tool-{index}-{8hex}` ids (Gemini's wire
  shape carries no native id).

### Deferred to alpha.12 (batch 3 of 3)

- Batch submitter primitive (`IBatchChatClient`) plus
  `OpenAiBatchChatClient` / `AnthropicBatchChatClient`.
- `IntegrationTests` skeleton round-trip suites (HTTP fakes for OpenAI
  Batch + Anthropic Batch APIs).
- v0.9h Pareto contract tests #11 (budget exhaustion mid-optimize),
  #14 (provider chaos), and #16 (semantic-cache concurrent fill).

Master plan §6.G / §6.H stay unchanged until batch 3 lands.

## [0.9.0-alpha.10] — 2026-04-26 — v0.9g routing primitives + semantic cache (batch 1 of 3)

First of three slices that complete v0.9g. This batch ships the routing
primitives (PSQ-1) and the in-memory semantic cache + replay-determinism
guard (PSQ-2). Test count rises from 4503 to 4578 (+75); coverage on the
new files is 100% line.

### Added — Routing (PSQ-1)

- `Ampelum.Routing.IRoutingStrategy`, `RoutingDecision`, `ProviderEndpoint`
  (sealed record). All gated behind
  `[Experimental("AMPELUM_V09_PREVIEW")]` and listed in
  `PublicAPI.Unshipped.txt`.
- Three concrete strategies: `Failover` (escalates down the fallback
  chain on transport failure), `LoadBalanced` (round-robin, lock-free
  via `Interlocked.Increment`), `KeyRotating` (bucketed rotation with a
  configurable `callsPerKey` burst size).
- `RoutedChatClient : IChatClient` decorator that consults the strategy
  per call, attempts the primary, walks the fallback chain on failure,
  and stamps `ChatResponse.AdditionalProperties["router.target.name"]`
  on every successful response. Streaming round-trips dispatch to the
  primary only (partial streams cannot be safely retried).
- DI extensions: `AmpelumBuilder.AddProviderEndpoint(...)` /
  `.AddRouting<TStrategy>()`. The latter replaces any prior
  `IChatClient` registration with the routed wrapper.

### Added — Semantic cache + hook (PSQ-2)

- `Ampelum.Caching.Semantic.ISemanticCache` contract and
  `InMemorySemanticCache` (sliding-window LRU; doubly-linked list +
  dictionary; coarse-grained lock).
- `SemanticCacheEntry` record stores Outputs + ModelId + prompt /
  completion tokens so a hit can reproduce the audit-chain entry of the
  original miss.
- `SemanticCacheHook : IHook` (Order = 5, before AuditHook at 10):
  short-circuits `OnBeforeAsync` on hit by setting `ctx.Outputs` AND
  restoring `AuditMetadataKeys.ModelId` /
  `HookMetadataKeys.PromptTokens` / `CompletionTokens` so the
  downstream AuditHook records a chain entry semantically identical to
  the original miss — the F4 replay-determinism guard.
- The hook bypasses the cache entirely when
  `HookMetadataKeys.AuditReplaying` is `true` so canonical recorded
  entries surface during audit replay.
- DI extension `AmpelumBuilder.AddSemanticCache(capacity = 256)` wires
  cache + hook in one call.

### Deferred to alpha.10 batch 2 + 3

Per the parent v0.9g plan, the following items remain unshipped and are
intentionally split out of this batch to keep the merge surface
reviewable:

- **Batch 2** — Rate limiting (`RateLimitingChatClient` + token-bucket
  partitioning, `Retry-After` honouring), circuit breaker
  (`CircuitBreakerChatClient` over Polly), and provider format adapters
  (`IProviderAdapter` + OpenAI / Anthropic / Gemini concretes,
  `ToolCallNormalizer` round-trip).
- **Batch 3** — Batch submitter primitive (`IBatchChatClient`) plus
  `OpenAiBatchChatClient` / `AnthropicBatchChatClient`,
  `Ampelum.ContractTests` round-trip suites, and the v0.9h Pareto
  contract tests #11 / #14 / #16 (budget exhaustion, provider chaos,
  cache concurrency). Master plan §6.G / §6.H stays unchanged until
  batch 3 lands.

## [0.9.0-alpha.9] — 2026-04-26 — v0.9g deadline integration

Wires the v0.9g deadline-propagation primitives shipped in alpha.8 into
`Predict<TSig>.ForwardAsync`, completing Task 9 of the v0.9g
cross-cutting plan. Tasks 2–8 + 11–13 (routing, semantic cache, rate
limit, circuit breaker, batch submitters, format adapters) and the
v0.9h Pareto + comparative bench suites remain deferred — see
"Deferred" below.

### Added — v0.9g recovery (Task 9 deadline integration)

- `Predict<TSig>.ForwardAsync` now invokes the internal
  `CheckDeadline` helper at every PSQ-5 phase boundary
  (`before-onbefore`, `post-onbefore`, `post-lm-call`, `post-parse`,
  `pre-onafter`, `post-onafter`), including the OnBefore short-circuit
  branch. `AmpelumContext.Deadline` and the per-call
  `HookContext.Metadata["deadline.utc"]` override (earlier of the two
  wins) are honoured. On a deadline crossing, the standard outer
  exception handler runs the `OnError` hook path and writes an error
  trace before propagating `DeadlineExceededException`.
- `tests/Ampelum.Tests/Deadline/PredictDeadlinePropagationTests.cs` —
  7 new integration tests (deadline-in-past, mid-OnBefore, mid-LM,
  per-call hook metadata override, no-deadline / future-deadline happy
  paths, short-circuit branch).

### Changed — `DeadlineExceededException` base type

- Now derives from `TimeoutException` instead of
  `OperationCanceledException`. PSQ-5 originally proposed OCE so
  `catch (OperationCanceledException)` would absorb deadline
  crossings, but the C# async-state-machine special-cases OCE-derived
  exceptions: `AsyncTaskMethodBuilder.SetException` calls
  `TrySetCanceled` regardless of cancellation-token match, which
  drops the derived type when callers observe via `Task.Exception` or
  `Should.ThrowAsync<T>`. `TimeoutException` is semantically correct
  (a deadline crossing IS a timeout) and preserves the concrete type
  across `await`. Test `Derives_From_OperationCanceledException`
  renamed to `Derives_From_TimeoutException` with rationale comment.
  Public surface (the type, ctor, properties) unchanged in shape.

### Pareto integration tests (v0.9h)

- None added in alpha.9. Pareto tests #11 / #14 / #16 (budget
  exhaustion, provider chaos, cache concurrency) targeted by the
  v0.9h plan for this release are deferred — see "Deferred to
  alpha.10" below.

### Deferred to v0.9.0-alpha.10

- v0.9g Tasks 2/3 (`IRoutingStrategy` + routing strategies +
  `KeyRotatingChatClient` + `RouterMetrics` + DI extensions).
- v0.9g Task 4 (`SemanticCachingChatClient` + cache stores +
  `CosineSimilarity` + replay determinism guard).
- v0.9g Task 5 (`RateLimitingChatClient` + `RateLimitedException` +
  `RetryAfterParser` + Polly v8 token bucket + Retry-After honouring +
  DI extension).
- v0.9g Task 6 (`CircuitBreakerChatClient` + Polly v8 bridge + DI).
- v0.9g Tasks 7/8 (`IBatchChatClient` + `OpenAiBatchChatClient` +
  `AnthropicBatchChatClient` + `BatchPollLoop` + ContractTests).
- v0.9g Tasks 11/12/13 (`IProviderAdapter` registry +
  OpenAI/Anthropic/Gemini adapters + `NormalizedToolCall` +
  `ToolCallNormalizer` + round-trip property test).
- v0.9h `Ampelum.IntegrationTests` project skeleton + fixture + skip
  predicate.
- v0.9h Pareto tests #11 (budget exhaustion), #14 (provider chaos),
  #16 (cache concurrency) — depend on v0.9g primitives above.
- v0.9h Pareto tests #1–10, #12–13, #15, #17–19.

### Deferred to v0.9.0-alpha.11

- v0.9h comparative bench suite — 15 scenarios + 6 competitor pins
  (SK / MEAI / AutoGen.NET / LangChain.NET / DSPy-py / LangChain-py)
  + 2 Python subprocess harnesses + ranking parser + 2 GitHub
  Actions workflows.

### Tests + coverage

`Ampelum.Tests` rises from 4496 → 4503 (+7 new
`PredictDeadlinePropagationTests`). `Ampelum.Testing` 100% line +
branch. `Ampelum` 99.2% line / 95.28% branch — pre-existing gap from
alpha.8 in `BatchedTraceSink`, `Realtime/WebSocketTransport`,
`Retrieval/Splitters/RecursiveCharacterSplitter`,
`Retrieval/Hybrid/CohereReranker` / `VoyageReranker`,
`Audit/BatchedAuditSink`. `Predict.cs` itself remains at 100% line
coverage including the new phase-boundary `CheckDeadline` calls.

## [0.9.0-alpha.8] — 2026-04-26 — v0.9g Cross-cutting Infra (partial)

Cross-cutting infrastructure foundation. Lays the metadata-key contract
and deadline-propagation primitives that the routing, semantic-cache,
rate-limiter, circuit-breaker, batch, and provider-adapter decorators
will plug into. **Decorator implementations and DI extension surface
are deferred to v0.9h** (risk-driven scope reduction during the v0.9g
session — see "Deferred" below).

### Added — Metadata keys (Task 1)

- `HookMetadataKeys.RateLimitKey`, `RouterTargetName`, `DeadlineUtc`,
  `AuditReplaying`, `SemanticCacheHit`, `SemanticCacheSimilarity` —
  well-known `HookContext.Metadata` keys for v0.9g cross-cutting
  decorators (PSQ-1 / PSQ-2 / PSQ-3 / PSQ-5).
- `Audit.AuditMetadataKeys.EventTypeRouterFailover`, `EventTypeSemcacheHit`,
  `EventTypeSemcacheStore`, `EventTypeRatelimitAcquired`,
  `EventTypeRatelimitRejected`, `EventTypeCircuitBreakerOpen`/`HalfOpen`/
  `Closed`, `EventTypeDeadlineExceeded` — `AuditEntry.EventType`
  constants emitted by future v0.9g/v0.9h decorators.
- `Audit.AuditMetadataKeys.RouterFrom`, `RouterTo`, `RouterErrorKind` —
  metadata keys on router-failover audit entries.

### Added — Deadline propagation primitives (Task 9, partial)

- `AmpelumContext.Deadline` (init-only `DateTimeOffset?`) — opt-in
  wall-clock deadline (PSQ-5).
- `AmpelumContext.WithDeadline(TimeSpan)` — convenience helper that
  returns a new context with `Deadline = UtcNow + duration`. Throws
  `ArgumentOutOfRangeException` for non-positive durations.
- `Exceptions.DeadlineExceededException` (sealed, derives from
  `OperationCanceledException`) — carries the crossed `Deadline` and
  the `Phase` at which it was observed.
- `Predict<TSig>.CheckDeadline(...)` (internal static helper) — resolves
  the effective deadline from `AmpelumContext.Deadline` and
  `HookContext.Metadata["deadline.utc"]` (the EARLIER of the two wins)
  and throws `DeadlineExceededException` when crossed.

### Added — Package pins (Task 1)

- `Polly` 8.5.0 — pre-pinned for v0.9h circuit-breaker decorator
  (PSQ-1 / PSQ-3 / PSQ-6).
- `System.Threading.RateLimiting` 9.0.0 — explicit pin (BCL-shipped) so
  NU1011 stays green when v0.9h consumes
  `PartitionedRateLimiter<HookContext>`.

### Deferred to v0.9h — risk-driven scope reduction

The original v0.9g plan defined 12 tasks across multi-provider routing, semantic caching, rate
limiting, circuit breakers, OpenAI/Anthropic batch APIs, and provider
format adapters. v0.9.0-alpha.8 ships the metadata-key contract and
deadline-propagation primitives (Tasks 1 + Task 9 partial). Tasks 2/3
(routing strategies + DI), Task 4 (semantic cache), Task 5 (rate
limiting), Task 6 (circuit breaker), Tasks 7/8 (batch primitives +
OpenAI/Anthropic adapters), and Tasks 11/12/13 (provider format
adapters + tool-call normalizer) are deferred to v0.9h. The
`Predict<TSig>.ForwardAsync` integration of `CheckDeadline` is also
deferred (helper is exposed internally and unit-tested directly so the
v0.9h integration lands as a one-line decorator without re-touching
the public surface).

### Tests + coverage

`Ampelum.Tests` rises from 4479 → 4496 (+17). 100 % line coverage on
`[Ampelum]*` + `[Ampelum.Testing]*` maintained.

## [0.9.0-alpha.7] — 2026-04-26 — v0.9f Tooling + Scaffolding

Developer-experience bundle. Ships the `ampelum` global CLI tool, 5
`dotnet new` persona templates, a Roslyn-Scripting REPL, the recipe
cookbook authoring framework, four pre-built `ITypedTool` NuGets, and
the AMP1010 free-form opt-in analyzer.

### Added — CLI (`Ampelum.Cli`, T1–T4, T10)

- `ampelum --version` / `--help` (System.CommandLine root).
- `ampelum new <template>` — wraps `dotnet new` with template name
  validation and `--name` / `--output` pass-through.
- `ampelum bench --signature <S> --output <dir>` — scaffolds a
  BenchmarkDotNet harness against a signature.
- `ampelum eval --signature <S> --dataset <path> --metric <m>` — runs
  `Evaluate<TSig>` and writes `report.md` + `report.json`.
- `ampelum repl` — opens the Roslyn-Scripting REPL.

### Added — Templates (`Ampelum.Templates`, T5–T9)

- `ampelum-backend` — ASP.NET Minimal-API + `Predict` + DI sample.
- `ampelum-researcher` — console + `BootstrapFewShot` + dataset stub.
- `ampelum-enterprise` — AOT-ready + Audit + Budget pre-wired.
- `ampelum-platform` — multi-tenant DI scope + provider routing stub.
- `ampelum-agent` — GroupChat with two agents + `ITypedTool` calculator.

### Added — REPL (`Ampelum.Repl`, T10)

- `ReplGlobals` exposing the active `AmpelumContext` as `Ctx`.
- `ReplHost.EvaluateAsync` chaining `CSharpScript.RunAsync` /
  `ContinueWithAsync` with `Ampelum` + `Ampelum.Testing` imports.
- `ReplLoop` recognising `:save <slot>` / `:load <slot>` / `:quit`;
  injectable `TextReader`/`TextWriter` for testability.
- Tab-completion via Roslyn Workspaces deferred to v1.0-rc1.

### Added — Recipe cookbook (T11)

- `tools/Ampelum.RecipeExtract` console extractor (in-repo; replaces the
  master plan's `dotnet script` mention so no global tool is required).
- `Ampelum.Recipes.Tests` BeforeCompile target runs the extractor on
  every build; generated `*.g.cs` files compile alongside hand-written
  tests with `TreatWarningsAsErrors=true`.
- First sample recipe `docs/recipes/01-hello-predict.md` driving
  `Predict<HelloSig>` against `FakeChatClient`.
- `Ampelum.Recipes.Tests.MetaTests` exercises the extractor directly.

### Added — Pre-built tools (T12)

- `Ampelum.Tools.WebSearch` — Brave-backed `IWebSearchProvider`
  (env `BRAVE_API_KEY`), POLC `network:egress`, `data:public-internet`.
- `Ampelum.Tools.Calculator` — pure-compute arithmetic
  (`DataTable.Compute`), POLC `compute:pure`, `data:none`.
- `Ampelum.Tools.Wikipedia` — REST summary lookup, no API key.
- `Ampelum.Tools.CodeInterpreter` — unsandboxed Roslyn-Scripting
  evaluator. **Host-process; do NOT run on untrusted input.** POLC
  `compute:host-process`, `risk:high`, `network:any`, `fs:any`.

### Added — Analyzer

- `Ampelum.Tools.AllowFreeFormAttribute` (assembly-level opt-in).
- AMP1010 `Amp1010FreeFormOptInAnalyzer`: warns when `[FreeForm]` is
  declared in an assembly missing `[assembly: AllowFreeForm]`. All four
  pre-built tool assemblies opt in by design.

### Test count

4773 tests passing (4724 prior + 49 new across REPL / CodeInterpreter /
Wikipedia / Recipes / Recipes meta / AMP1010 / CLI repl wiring).

---

## [0.9.0-alpha.1] — 2026-04-25

First v0.9 sub-milestone: multi-agent group chat + conversation memory +
multi-modal image input + Realtime WebSocket transport. The new public
surface is gated behind `[Experimental("AMPELUM_V09_PREVIEW")]` and may
change before promotion to stable in `v1.0-rc1`.

### Added — Agents

- `Ampelum.Agents.AgentMessage` (record): sender / role / content with
  optional `Handoff`, `IsTerminal`, `Timestamp`, and free-form metadata.
- `Ampelum.Agents.ConversationContext`: defensive read-only snapshot of
  history + memory + turn index passed to selectors and agents.
- `Ampelum.Agents.IAgent`: minimal contract — `Name`, `Role`,
  `ReplyAsync(ConversationContext, CancellationToken)`.
- `Ampelum.Agents.GroupChat` orchestrator with `TerminationReason`
  (TerminalMessage / MaxTurnsReached / HandoffToUnknown) and
  `GroupChatResult` record.
- `ISpeakerSelector` + three implementations:
  - `RoundRobinSelector` (deterministic rotation).
  - `RoleBasedSelector` (rotation by role list).
  - `LlmDrivenSelector` (LLM-coordinated; falls back to round-robin
    on unknown agent name with a logged warning).
- Handoff routing: `AgentMessage.Handoff` overrides the next selector
  pick; unknown handoff targets terminate the chat.

### Added — Conversation memory

- `Ampelum.Agents.Memory.IConversationMemory` contract.
- `InMemoryConversationMemory` (FIFO ring buffer with strict capacity).
- `VectorConversationMemory` (k-NN over `IVectorStore` +
  `IEmbeddingGenerator`; in-memory timestamp index for `GetRecentAsync`).
- `EpisodicMemory` (LLM-summarises the oldest `windowSize` messages
  when buffer exceeds `threshold`).
- `CompositeConversationMemory` (fans out append, defers `GetRecentAsync`
  to the first tier, merges + dedupes + trims `QueryAsync`).

### Added — Tools

- `Ampelum.Agents.Tools.AgentToolResult`,
  `IAgentTool : ITypedTool<AgentToolResult>`, `IAgentToolRegistry`,
  `AgentToolRegistry` (concurrent, indexed by `(Name, Version)` with
  `ListByCategory` / `All`). No sandbox in this milestone (PSQ-6).
- `Ampelum.IReActTool` non-generic façade + `ReAct<TSig>` constructor
  overload accepting `IReadOnlyList<IReActTool>` (typed-tool
  integration; existing `IReadOnlyList<AIFunction>` overload unchanged).

### Added — Multi-modal input

- `Predict<TSig>` routes `byte[]` `[Input]` fields to MEAI `DataContent`
  (media type `image/png`) and `Uri` `[Input]` fields to `UriContent`
  (media type `image/*`). Existing text-only signatures unaffected.
- Output `byte[]` / `Uri` fields throw `NotSupportedException`; image
  generation output is deferred to `v1.0-rc1` (PSQ-9).
- `System.Uri` added to `SignatureMetadataCache`'s supported field-type
  whitelist.

### Added — Realtime transport

- `Ampelum.Realtime.IRealtimeTransport` minimal WebSocket-shaped
  abstraction.
- `RealtimeChatClient : IChatClient` thin wrapper that performs a
  `session.update` + `response.create` send and accumulates
  `response.text.delta` frames until `response.done`.
- `WebSocketTransport` adapter over `System.Net.WebSockets.ClientWebSocket`
  (covered by ContractTests follow-up; excluded from the v0.9a unit
  coverage gate).
- Streaming response (audio path) currently throws
  `NotSupportedException`; deferred per PSQ-8.

### Added — DI

- `AmpelumBuilder` extensions: `AddAgents()` (TryAdd
  `IAgentToolRegistry` + default `ISpeakerSelector` as
  `RoundRobinSelector`), `AddConversationMemory<T>()`,
  `AddRealtimeChatClient(Uri, string)`.

### Notes

- Public surface is `[Experimental("AMPELUM_V09_PREVIEW")]`-attributed
  and lands in `PublicAPI.Unshipped.txt`. Promote to `Shipped.txt` and
  drop the diagnostic at `v1.0-rc1`.
- Test count: 4047 → **4058** passing on full suite; line coverage
  99.9%+ on `[Ampelum]*` (gap consists of pre-existing defensive
  `catch` blocks in `BatchedTraceSink` / `BatchedAuditSink` and the
  `WebSocketTransport` adapter excluded via `ExcludeByFile`).

### Deferred (tracked for `v1.0-rc1`)

- Audit rows 3.3 (MCP tool integration), 3.8 (HITL approval gates),
  11.2 (image generation output), per the v0.9 master plan §6.A.

