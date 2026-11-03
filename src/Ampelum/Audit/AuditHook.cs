using System.Diagnostics;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Hook that records each forward call as an <see cref="AuditEntry"/> into an <see cref="IAuditSink"/>.
/// Joins the v0.6a hook pipeline at <c>Order=10</c> (<c>Order=51</c> in replay mode, after the PII hook). Spec Â§4.3.
/// In replay mode (PSQ-5), <c>OnBefore</c> serves recorded outputs by
/// <c>(ModuleName, hash(canonical Inputs))</c> and short-circuits the LM call.
/// </summary>
/// <remarks>
/// <para><b>Thread safety (v1.0-rc1):</b> All <c>OnAfterAsync</c> callers that append entries are
/// serialised via <c>_appendGate</c> (a <see cref="System.Threading.SemaphoreSlim"/>). This
/// eliminates the race where two concurrent hooks read the same <c>NextSequence</c> then attempt
/// out-of-order <c>AppendAsync</c> calls. The last-appended entry hash is cached so that
/// <c>PrevHash</c> lookup is O(1) instead of enumerating the entire audit log.</para>
/// </remarks>
public sealed class AuditHook : IHook
{
    readonly IAuditSink _sink;
    readonly bool _replayMode;
    readonly object _replayGate = new();
    /// <summary>
    /// One gate per sink, shared by every hook over it, so two hooks never read the same NextSequence.
    /// The gate is not reentrant: a sink must not call back into an <see cref="AuditHook"/> on itself
    /// from <see cref="IAuditSink.AppendAsync"/>, or the append waits for itself.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IAuditSink, SemaphoreSlim> AppendGates = new();
    readonly SemaphoreSlim _appendGate;
    byte[]? _lastAppendedHash;
    long _lastAppendedSequence;
    Dictionary<string, Queue<AuditEntry>>? _replayIndex;

    /// <summary>Creates a new audit hook bound to <paramref name="sink"/>.</summary>
    /// <param name="sink">Audit sink that receives appended entries (record mode) or supplies replay entries.</param>
    /// <param name="replayMode">When true, <c>OnBefore</c> populates <c>ctx.Outputs</c> from the recorded entry queue.</param>
    public AuditHook(IAuditSink sink, bool replayMode = false)
    {
        _sink = sink;
        _replayMode = replayMode;
        _appendGate = AppendGates.GetValue(sink, static _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc/>
    public int Order => _replayMode ? ReplayOrder : 10;

    // Replay looks the recorded entry up by the inputs that were persisted, which can be the redacted
    // form, so the lookup must run after PiiRedactionHook (50) has published it.
    const int ReplayOrder = 51;

    /// <inheritdoc/>
    public async ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        if (!_replayMode) return;
        var index = await EnsureIndexAsync(ct);
        var key = MakeKey(ctx.ModuleName, PersistedInputs(ctx));
        AuditEntry entry;
        lock (_replayGate)
        {
            if (!index.TryGetValue(key, out var q) || q.Count == 0)
                throw new AuditReplayMissingException(ctx.ModuleName);
            entry = q.Dequeue();
        }
        ctx.Outputs = entry.Outputs;
    }

    /// <inheritdoc/>
    public async ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        if (_replayMode) return;
        // PII invariant (v0.9c): if PiiRedactionHook (Order=50) ran first, it published
        // a redacted Inputs projection under "pii.redacted_inputs". Hash + persist the
        // redacted form. See docs/security/threat-model-v1.md Â§6.
        var inputs = PersistedInputs(ctx);
        var outputs = ctx.Outputs ?? new Dictionary<string, object?>();
        // Predict publishes these under HookMetadataKeys; a hook that sets the audit keys takes precedence.
        var modelId = MetadataValue<string>(ctx, AuditMetadataKeys.ModelId, HookMetadataKeys.ModelId) ?? "unknown";
        var pt = MetadataValue<long?>(ctx, AuditMetadataKeys.PromptTokens, HookMetadataKeys.PromptTokens) ?? 0L;
        var ctk = MetadataValue<long?>(ctx, AuditMetadataKeys.CompletionTokens, HookMetadataKeys.CompletionTokens) ?? 0L;
        var ts = DateTimeOffset.UtcNow;
        // v1.0-rc1.alpha.5 (Gap 2): propagate hook-set EventType discriminator.
        // Default "forward.completed" matches the only kind AuditHook produced before alpha.5.
        var eventType = ctx.Metadata.TryGetValue(AuditMetadataKeys.EventType, out var et) && et is string ets
            && !string.IsNullOrEmpty(ets) ? ets : "forward.completed";
        // v1.0-rc1.alpha.13: resolve TraceId from metadata key, then ambient Activity,
        // then "" as fallback. New rows ship as SchemaVersion=3 with TraceId folded into
        // the chain hash. Pre-alpha.13 rows (V1/V2) have no TraceId field and verify
        // under their respective algorithms without change.
        var traceId = ctx.Metadata.TryGetValue(AuditMetadataKeys.TraceId, out var tid) && tid is string ts2
            && !string.IsNullOrEmpty(ts2) ? ts2
            : Activity.Current?.TraceId.ToHexString() ?? "";

        // Serialise sequence assignment + prev-hash resolution + entry construction + append
        // under a single gate so that concurrent callers never read the same NextSequence.
        await _appendGate.WaitAsync(ct);
        try
        {
            var seq = _sink.NextSequence;
            // The cached hash is only good when this hook wrote the entry just before this one; after a
            // reopened file, another hook or queued entries the sink is asked for its newest hash.
            var prev = seq == 0 ? AuditChainHasher.ZeroHash
                : _lastAppendedHash is not null && _lastAppendedSequence == seq - 1 ? _lastAppendedHash
                : await LastEntryHash.OfAsync(_sink, ct);
            var entry = new AuditEntry
            {
                SchemaVersion = 3,
                EventType = eventType,
                TraceId = traceId,
                Sequence = seq,
                Timestamp = ts,
                ModuleName = ctx.ModuleName,
                ModelId = modelId,
                Inputs = inputs,
                Outputs = outputs,
                PromptTokens = pt,
                CompletionTokens = ctk,
                PrevHash = prev,
                EntryHash = AuditChainHasher.ComputeV3(seq, ts, ctx.ModuleName, eventType, modelId, inputs, outputs, pt, ctk, traceId, prev),
            };
            await _sink.AppendAsync(entry, ct);
            _lastAppendedHash = entry.EntryHash;
            _lastAppendedSequence = seq;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        => ValueTask.CompletedTask;

    static T? MetadataValue<T>(HookContext ctx, string key, string fallbackKey) =>
        ctx.Metadata.TryGetValue(key, out var v) && v is T t ? t
        : ctx.Metadata.TryGetValue(fallbackKey, out var f) && f is T ft ? ft
        : default;

    static IReadOnlyDictionary<string, object?> PersistedInputs(HookContext ctx) =>
        ctx.Metadata.TryGetValue("pii.redacted_inputs", out var ri)
            && ri is IReadOnlyDictionary<string, object?> redactedInputs
                ? redactedInputs
                : ctx.Inputs;

    async ValueTask<Dictionary<string, Queue<AuditEntry>>> EnsureIndexAsync(CancellationToken ct)
    {
        if (_replayIndex is not null) return _replayIndex;
        var index = new Dictionary<string, Queue<AuditEntry>>(StringComparer.Ordinal);
        await foreach (var e in _sink.EnumerateAsync(ct))
        {
            var k = MakeKey(e.ModuleName, e.Inputs);
            if (!index.TryGetValue(k, out var q)) { q = new Queue<AuditEntry>(); index[k] = q; }
            q.Enqueue(e);
        }
        lock (_replayGate) _replayIndex ??= index;
        return _replayIndex;
    }

    // DO NOT CHANGE: MakeKey uses Compute (V1 shape) by design. It hashes only
    // moduleName + inputs so that replay lookup is stable across SchemaVersion upgrades.
    // V2/V3 fields (EventType, TraceId) must not affect replay key derivation.
    static string MakeKey(string moduleName, IReadOnlyDictionary<string, object?> inputs)
    {
        var hash = AuditChainHasher.Compute(
            sequence: 0,
            timestamp: DateTimeOffset.MinValue,
            moduleName: moduleName,
            modelId: "",
            inputs: inputs,
            outputs: new Dictionary<string, object?>(),
            promptTokens: 0,
            completionTokens: 0,
            prevHash: AuditChainHasher.ZeroHash);
        return $"{moduleName}|{Convert.ToHexString(hash)}";
    }
}