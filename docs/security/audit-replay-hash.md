# Audit hash contract — `EntryHash` vs `ReplayHash`

Added in v1.0-rc1.alpha.10 (Gap 1 closure); revised in alpha.11 to move
`ReplayHash` off `AuditEntry` and onto an extension method. This doc
explains why audit entries expose **two** hashes, what each is the right
tool for, and the specific design fork the milestone took.

## TL;DR

| Property | Covers | Diverges across two calls when… | Use it for |
|---|---|---|---|
| `AuditEntry.EntryHash` (stored) | `(Sequence, Timestamp.Ticks, ModuleName, ModelId, Inputs, Outputs, PromptTokens, CompletionTokens, PrevHash)` — full row, byte-canonical | always (Timestamp + LM nondeterminism) | forward integrity / tamper detection |
| `entry.GetReplayHash()` (derived) | `(ModuleName, EventType, ModelId, Inputs canonical)` — input side only | only when the *prompt* changes | streaming↔non-streaming equivalence; cross-call cache keys |

## Why two hashes

`EntryHash` is the SHA-256 of the canonical byte representation of the entire
audit row, with `PrevHash` of the previous row mixed in. It is the
forward-integrity primitive: if any byte of any row in a chain changes, every
hash from that row forward also changes, so a chain walker (`AuditChainVerifier`)
can pinpoint the break.

That property comes at a cost: two LM calls over identical inputs **never**
produce equal `EntryHash` values. Two reasons:

1. `Timestamp.Ticks` is captured at `OnAfter` time — wall-clock skew between
   any two calls is large enough that the high-resolution Tick differs.
2. The downstream LM is non-deterministic. Identical prompts produce different
   `Outputs` text and different `PromptTokens`/`CompletionTokens` counts.

Pareto integration test #5 — streaming forward — is the canonical case where
this hurts. The user-facing claim being tested is: *"streaming a forward
produces a final value equivalent to a non-streaming forward over the same
input."* The natural reach is `streaming.Audit[0].EntryHash ==
nonStreaming.Audit[0].EntryHash`. That assertion is incoherent under the two
sources of nondeterminism above. Pareto05 has lived since alpha.5 with a
weaker equivalence relation: tuple equality on `(Inputs canonical,
ModuleName, EventType)`. That relation works but doesn't compose with the
verifier — there is no single-call check `IsValid(entry)` that asserts it.

`ReplayHash` exists to give that equivalence a real, type-safe primitive.

## What `ReplayHash` covers (and what it deliberately doesn't)

`ReplayHash = SHA256(ModuleName ‖ 0 ‖ EventType ‖ 0 ‖ ModelId ‖ 0 ‖ canonical(Inputs))`,
returned as lowercase hex (64 chars).

Outputs, token counts, Sequence, PrevHash, and Timestamp are deliberately
**out of scope**:

- `Outputs` and tokens vary across LM calls with the same inputs (LM
  nondeterminism). Folding them in defeats the purpose of having a
  cross-call equivalence hash.
- `Sequence` and `PrevHash` are chain-position metadata. The same logical
  forward call appearing at sequence 7 in one session and sequence 12 in
  another should still match.
- `Timestamp` is wall-clock advisory. Folding it in defeats the purpose;
  see "Why two hashes" above.

`ReplayHash` answers exactly one question: *were these two calls dispatched
against the same input, under the same module, the same event-type, the
same target model?* Whatever the LM said back is irrelevant to that
question.

## The design fork

Three options were on the table for closing Gap 1:

- **(a) Fold `Timestamp` out of `EntryHash`.** Simpler; one hash. But makes
  the chain integrity model strictly weaker — replay can no longer detect
  "this entry was written at a different time" attacks. Rejected: trades
  long-lived integrity guarantees for a single test scenario.
- **(b) Add a companion field that excludes the nondeterministic columns.**
  Additive, no breakage. Two sub-options under (b):
  - (b1) Exclude `Outputs` + tokens + Timestamp (input-side only).
  - (b2) Exclude `Timestamp` only (literal reading of the original task).
- **(c) Reject the equivalence as fundamentally incoherent.** Honest, but
  means streaming↔non-streaming has no first-class testability primitive
  beyond "smell-test with `.ShouldContain`".

**We picked (b1).** (b2) is what the original gap-closure task literally
specified, but `Outputs` are LM-nondeterministic, so a `ReplayHash` that
includes them would *not* be equal across two streaming/non-streaming runs
of the same input — the whole point of the field. The honest read of the
spec — and the one that lets Pareto05 promote to a real assertion — is that
`ReplayHash` covers only the input-determinism dimensions.

## Test promotion

Pareto05's real-LM variant (`Pareto05_StreamingForward`) keeps its existing
weak equivalence relation: it runs only when `OPENROUTER_API_KEY` is set,
and the LM nondeterminism prevents asserting `Outputs` equality.

The new fake-LM variant (`Pareto05_StreamingForward_Fake`) runs in CI without
a key. It enqueues the same canonical adapter response on two
`FakeChatClient` instances, runs one streaming forward and one non-streaming
forward over the same `QaSig` input, and asserts:

```csharp
streamEntries[0].GetReplayHash().ShouldBe(nonStreamEntries[0].GetReplayHash());
```

This is the regression gate. Any change to `AuditChainHasher.ComputeReplay`
or the `AuditHook` wiring that breaks streaming↔non-streaming equivalence
fails this test in CI.

## Verifier behaviour

`AuditChainVerifier.VerifyAsync` validates `EntryHash` and `PrevHash`
linkage row by row. As of alpha.11, `ReplayHash` is **derived** from
`(ModuleName, EventType, ModelId, Inputs)` rather than stored — the
verifier no longer ranges over it because there is no persisted value
to compare against. The accessor (`AuditEntryReplay.GetReplayHash`)
recomputes the canonical hash on demand from the four input-side fields.

### EventType tamper detection (alpha.12)

`AuditEntry` carries a `SchemaVersion` discriminator selecting the
hashing algorithm used by both the `AuditHook` emitter and the
`AuditChainVerifier`:

| `SchemaVersion` | Hash shape | Covers `EventType`? |
|---|---|---|
| `1` (legacy / pre-alpha.12) | `(Sequence, Timestamp.Ticks, ModuleName, ModelId, Inputs, Outputs, PromptTokens, CompletionTokens, PrevHash)` | No |
| `2` (alpha.12+) | `(Sequence, Timestamp.Ticks, ModuleName, EventType, ModelId, Inputs, Outputs, PromptTokens, CompletionTokens, PrevHash)` — `EventType` interleaved between `ModuleName` and `ModelId` with NUL separators | **Yes** |

`AuditChainHasher.ComputeV2` produces the v2 bytes; `AuditChainHasher.Compute`
remains for v1. The verifier dispatches on `entry.SchemaVersion` per-row,
so **mixed v1+v2 chains validate cleanly** — each row recomputes under its
own algorithm.

**Migration:** the C# default for `AuditEntry.SchemaVersion` is `1`. STJ
deserialization resolves a missing field to the C# default, so pre-alpha.12
serialized rows on disk load as v1 and verify under the v1 algorithm
without any custom converter. New rows produced by `AuditHook.OnAfterAsync`
explicitly set `SchemaVersion = 2`, fold `EventType` into `EntryHash`, and
get full tamper detection.

**Result:** the alpha.11 trade-off — "EventType tampering is not detected
by the chain verifier" — is closed for all rows written from alpha.12
forward. Pre-alpha.12 rows retain their original guarantees (no weakening,
no rewrite of historical hashes).

### Why no `[Experimental]` on `SchemaVersion`

`AuditEntry.SchemaVersion` is **not** marked `[Experimental("AMPELUM_V09_PREVIEW")]`,
even though it is net-new public surface during the v1.0-rc1 milestone.
This is the same trap that drove the alpha.10 → alpha.11 pivot: the STJ
source generator that `AuditEntryJson` registers reads every property of
`AuditEntry` and emits accessor code that triggers the experimental
diagnostic on every consumer of the serialization context. Marking
`SchemaVersion` experimental would re-introduce that propagation across
the audit serialization path. `PublicAPI.Unshipped.txt` is the
surface-stability marker; the property itself stays plain to keep STJ
source-gen output diagnostic-clean.

## Surface stability

`AuditEntryReplay` and `AuditEntryReplay.GetReplayHash` are
`[Experimental("AMPELUM_V09_PREVIEW")]`-attributed and listed in
`PublicAPI.Unshipped.txt` under the `[AMPELUM_V09_PREVIEW]` marker.

**alpha.10 → alpha.11 pivot rationale.** alpha.10 added `ReplayHash` as
a direct property on `AuditEntry`. The `[Experimental]` attribute was
dropped because the `System.Text.Json` source generator that
`Ampelum.Audit.AuditEntryJson` registers for serialization reads every
property of `AuditEntry` and emits accessor code that triggers the
`AMPELUM_V09_PREVIEW` diagnostic on every consumer of the JSON context.
That defeated the point of the marker — `[Experimental]` is meant to
enforce, not signal.

alpha.11 moves the hash off the serialized type entirely. `AuditEntry`
no longer has a `ReplayHash` property. The hash is exposed via an
extension method on a separate `[Experimental]`-attributed static class.
STJ source-gen no longer sees a property to propagate, so the marker
can be enforced consistently across the codebase without `<NoWarn>` in
library code.

Promotion from Unshipped to Shipped follows the standard milestone-tail
path at v1.0 tag time.
