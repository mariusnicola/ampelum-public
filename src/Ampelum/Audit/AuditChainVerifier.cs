namespace SignalGrid.Ampelum.Audit;

/// <summary>Result of verifying a hash-chained audit log. Spec Â§4.4.</summary>
/// <param name="IsValid">True iff the chain is intact end-to-end.</param>
/// <param name="BreakAtSequence">Sequence at which verification first failed; null when <paramref name="IsValid"/> is true.</param>
/// <param name="Reason">Human-readable diagnostic message.</param>
public sealed record AuditChainVerificationResult(bool IsValid, long? BreakAtSequence, string Reason);

/// <summary>
/// Client-side hash-chain verifier. Walks the sink in sequence and recomputes each
/// <see cref="AuditEntry.EntryHash"/>, checking <see cref="AuditEntry.PrevHash"/> linkage.
/// Spec Â§4.4; OQ-2 â€” server-side pgcrypto verification deferred.
/// </summary>
public static class AuditChainVerifier
{
    /// <summary>Walk <paramref name="sink"/> and verify every entry's hash and prev-hash linkage.</summary>
    /// <param name="sink">Audit sink to walk via <see cref="IAuditSink.EnumerateAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="AuditChainVerificationResult"/> describing pass/fail and the break sequence.</returns>
    public static async ValueTask<AuditChainVerificationResult> VerifyAsync(IAuditSink sink, CancellationToken ct)
    {
        long expected = 0;
        byte[] prev = AuditChainHasher.ZeroHash;
        await foreach (var entry in sink.EnumerateAsync(ct))
        {
            if (entry.Sequence != expected)
                return new(false, entry.Sequence, $"Sequence break: expected {expected}, got {entry.Sequence}.");
            if (!entry.PrevHash.AsSpan().SequenceEqual(prev))
                return new(false, entry.Sequence, "PrevHash does not match previous EntryHash.");
            // v1.0-rc1.alpha.12: dispatch on SchemaVersion. v1 rows hash the legacy
            // shape (no EventType); v2 rows fold EventType into the canonical bytes,
            // restoring tamper detection on the discriminator. Mixed v1+v2+v3 chains
            // verify because each row uses its own algorithm.
            // v1.0-rc1.alpha.13: v3 rows additionally fold TraceId after completionTokens.
            byte[] recomputed = entry.SchemaVersion switch
            {
                3 => AuditChainHasher.ComputeV3(
                    entry.Sequence, entry.Timestamp, entry.ModuleName, entry.EventType, entry.ModelId,
                    entry.Inputs, entry.Outputs, entry.PromptTokens, entry.CompletionTokens, entry.TraceId, entry.PrevHash),
                2 => AuditChainHasher.ComputeV2(
                    entry.Sequence, entry.Timestamp, entry.ModuleName, entry.EventType, entry.ModelId,
                    entry.Inputs, entry.Outputs, entry.PromptTokens, entry.CompletionTokens, entry.PrevHash),
                _ => AuditChainHasher.Compute(
                    entry.Sequence, entry.Timestamp, entry.ModuleName, entry.ModelId,
                    entry.Inputs, entry.Outputs, entry.PromptTokens, entry.CompletionTokens, entry.PrevHash),
            };
            if (!entry.EntryHash.AsSpan().SequenceEqual(recomputed))
                return new(false, entry.Sequence, "EntryHash does not match recomputed hash (tampered).");
            // v1.0-rc1.alpha.12: ReplayHash is derived on demand from
            // (ModuleName, EventType, ModelId, Inputs) via AuditEntryReplay. EventType
            // tamper detection is now covered by EntryHash for SchemaVersion>=2 rows.
            prev = entry.EntryHash;
            expected++;
        }
        return new(true, null, "OK");
    }
}
