namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// One immutable, hash-chained entry in the Ampelum audit log. Spec Â§4.1.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>
    /// v1.0-rc1.alpha.12: hash-shape schema version for this row. Selects the
    /// <see cref="AuditChainHasher"/> algorithm used to compute and verify
    /// <see cref="EntryHash"/>.
    /// <list type="bullet">
    ///   <item><description><c>1</c> â€” alpha.5 shape: <c>(Sequence, Timestamp.Ticks,
    ///   ModuleName, ModelId, Inputs, Outputs, PromptTokens, CompletionTokens,
    ///   PrevHash)</c>. <see cref="EventType"/> is excluded.</description></item>
    ///   <item><description><c>2</c> â€” alpha.12 shape: identical to v1 but with
    ///   <see cref="EventType"/> folded in between <c>ModuleName</c> and
    ///   <c>ModelId</c>, restoring tamper detection.</description></item>
    /// </list>
    /// <para><b>Default is <c>1</c>.</b> This is deliberate: serialized rows written
    /// before alpha.12 have no <c>SchemaVersion</c> field, and STJ deserialization
    /// resolves the missing field to the C# default. Defaulting to <c>1</c> makes
    /// those legacy rows verify under the v1 algorithm without a custom converter.
    /// New rows produced by <see cref="AuditHook"/> (alpha.12+) explicitly set
    /// <see cref="SchemaVersion"/>=<c>2</c>.</para>
    /// <para><b>No <c>[Experimental]</c> attribute:</b> the alpha.11 design decision
    /// established that STJ source-gen reads every property of <see cref="AuditEntry"/>
    /// and propagates any <c>[Experimental]</c> diagnostic to every consumer of
    /// <c>AuditEntryJson</c>. Marking <see cref="SchemaVersion"/> experimental would
    /// re-introduce that propagation on the audit serialization path. The property is
    /// listed under <c>[AMPELUM_V09_PREVIEW]</c> in <c>PublicAPI.Unshipped.txt</c> as
    /// the surface-stability marker; the property itself stays plain to keep STJ
    /// source-gen output diagnostic-clean. See <c>docs/security/audit-replay-hash.md</c>.</para>
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// v1.0-rc1.alpha.5 (Gap 2): discriminator for the kind of audit event this entry
    /// represents. Defaults to <c>"forward.completed"</c> for the legacy hash-chained
    /// row produced by <see cref="AuditHook.OnAfterAsync"/>. Hooks set the value via
    /// <c>HookContext.Metadata[<see cref="AuditMetadataKeys.EventType"/>]</c>;
    /// <see cref="AuditHook"/> propagates that value into this property. Well-known
    /// values are listed on <see cref="AuditMetadataKeys"/> (e.g.
    /// <see cref="AuditMetadataKeys.EventTypeAgentHandoff"/>,
    /// <see cref="AuditMetadataKeys.EventTypeRouterFailover"/>).
    /// <para>v1.0-rc1.alpha.12: rows with <see cref="SchemaVersion"/>=<c>2</c> include
    /// <see cref="EventType"/> in the canonical hash, restoring tamper detection.
    /// Rows with <see cref="SchemaVersion"/>=<c>1</c> (legacy, including pre-alpha.5
    /// rows that defaulted EventType to <c>"forward.completed"</c>) continue to
    /// verify under the v1 algorithm that excludes EventType.</para>
    /// </summary>
    public string EventType { get; init; } = "forward.completed";

    /// <summary>
    /// v1.0-rc1.alpha.13: W3C TraceContext trace-id (32 lowercase hex chars) that scopes
    /// this audit row to a logical request / turn / asset trace tree. Default <c>""</c>
    /// applies when the row was authored before alpha.13 (STJ resolves the missing field
    /// to the C# default) or when no metadata key / ambient <see cref="System.Diagnostics.Activity"/>
    /// was available at append time.
    /// <para><b>Format invariant (V3-only):</b> when <see cref="SchemaVersion"/>=3, the
    /// chain hash folds the raw bytes of this string at a stable position. The hasher does
    /// not validate format; the caller is responsible for ensuring W3C-compliant
    /// 32-lowercase-hex when interop with OTel matters.</para>
    /// <para><b>No <c>[Experimental]</c> attribute</b> â€” same rationale as
    /// <see cref="SchemaVersion"/>: STJ source-gen would propagate the diagnostic to every
    /// consumer of <c>AuditEntryJson</c>. Surface stability is tracked via
    /// <c>PublicAPI.Unshipped.txt</c> under <c>AMPELUM_V09_PREVIEW</c>.</para>
    /// <para><b>Backing field initializer:</b> the private field <c>_traceId</c> is
    /// initialized to <c>""</c>. STJ source-gen for missing JSON fields skips the
    /// <c>init</c> setter entirely, leaving the field at its initialized value. This
    /// ensures V1/V2 rows (no <c>TraceId</c> on disk) deserialize to <c>""</c>.
    /// The getter always returns non-null.</para>
    /// </summary>
    public string TraceId
    {
        get => _traceId;
        init => _traceId = value ?? "";
    }
    string _traceId = "";

    /// <summary>Monotonic, zero-based sequence assigned by the sink.</summary>
    public required long Sequence { get; init; }
    /// <summary>UTC timestamp of the recorded forward call.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Module type name producing the call (e.g. <c>Predict&lt;SimpleSig&gt;</c>).</summary>
    public required string ModuleName { get; init; }
    /// <summary>Model identifier as reported by the chat client (PSQ-4).</summary>
    public required string ModelId { get; init; }
    /// <summary>Canonicalised input dictionary (JSON-primitive values only â€” PSQ-3).</summary>
    public required IReadOnlyDictionary<string, object?> Inputs { get; init; }
    /// <summary>Canonicalised output dictionary (JSON-primitive values only â€” PSQ-3).</summary>
    public required IReadOnlyDictionary<string, object?> Outputs { get; init; }
    /// <summary>Prompt tokens consumed by this call.</summary>
    public required long PromptTokens { get; init; }
    /// <summary>Completion tokens emitted by this call.</summary>
    public required long CompletionTokens { get; init; }
    /// <summary>SHA-256 of the previous entry's <see cref="EntryHash"/>; 32 zero bytes for sequence 0 (PSQ-2).</summary>
    public required byte[] PrevHash { get; init; }
    /// <summary>SHA-256 of the canonical byte representation of this entry (Â§4.2).</summary>
    public required byte[] EntryHash { get; init; }
}
