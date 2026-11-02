using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// v1.0-rc1.alpha.11: input-side replay-hash accessor for <see cref="AuditEntry"/>.
/// Lives off the serialized type so the <c>[Experimental("AMPELUM_V09_PREVIEW")]</c>
/// marker can be enforced without the STJ source generator propagating the diagnostic
/// to every consumer of <see cref="AuditEntry"/>'s JSON serialization context.
///
/// <para>The hash answers exactly one question: <i>were these two calls dispatched
/// against the same input, under the same module, the same event-type, and the same
/// target model?</i> Outputs, token counts, sequence, prev-hash, and timestamp are
/// deliberately out of scope. See <c>docs/security/audit-replay-hash.md</c>.</para>
///
/// <para>Returns lowercase hex (64 chars) of <c>SHA256(ModuleName â€– 0 â€– EventType
/// â€– 0 â€– ModelId â€– 0 â€– canonical(Inputs))</c>.</para>
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class AuditEntryReplay
{
    /// <summary>
    /// Compute the input-side replay hash for <paramref name="entry"/>. Pure function
    /// of <see cref="AuditEntry.ModuleName"/>, <see cref="AuditEntry.EventType"/>,
    /// <see cref="AuditEntry.ModelId"/>, and <see cref="AuditEntry.Inputs"/>.
    /// </summary>
    /// <param name="entry">Audit entry to derive the replay hash for.</param>
    /// <returns>Lowercase hex SHA-256 of the canonical input projection.</returns>
    public static string GetReplayHash(this AuditEntry entry)
        => AuditChainHasher.ComputeReplay(entry.ModuleName, entry.EventType, entry.ModelId, entry.Inputs);

    /// <summary>
    /// v1.0-rc1.alpha.13: return the W3C TraceContext trace-id stored on
    /// <paramref name="entry"/>. Returns <see cref="AuditEntry.TraceId"/> directly;
    /// lives here so <c>[Experimental]</c> gating is consistent with
    /// <see cref="GetReplayHash"/> and does not propagate through the STJ source-gen
    /// context for <see cref="AuditEntry"/>.
    ///
    /// <para>Returns <c>""</c> for V1/V2 rows authored before alpha.13.</para>
    /// </summary>
    /// <param name="entry">Audit entry to read the trace-id from.</param>
    /// <returns>W3C trace-id string, or <c>""</c> when absent.</returns>
    public static string GetTraceId(this AuditEntry entry) => entry.TraceId;
}
