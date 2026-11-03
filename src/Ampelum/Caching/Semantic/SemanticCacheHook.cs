using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Hook that wires an <see cref="ISemanticCache"/> into the forward pipeline. On cache hit
/// (<see cref="OnBeforeAsync"/>) the hook short-circuits the LM call by populating
/// <see cref="HookContext.Outputs"/>, AND restores the <c>HookMetadataKeys.ModelId</c> /
/// <c>PromptTokens</c> / <c>CompletionTokens</c> values so the downstream <see cref="AuditHook"/>
/// records an audit chain entry semantically identical to the original cache-miss entry â€” the
/// PSQ-2 F4 replay-determinism guard. On miss (<see cref="OnAfterAsync"/>) the hook captures the
/// parsed outputs together with the same metadata for future replays (PSQ-2, v0.9g).
/// </summary>
/// <remarks>
/// Order (52) is just after the PII redaction hook (50) and the replay-mode audit hook (51), so on a hit that hook's OnBefore has already
/// published the redacted inputs that <see cref="AuditHook"/> records; hooks ordered above 51 do not
/// run their OnBefore on a hit. When the
/// <c>HookMetadataKeys.AuditReplaying</c> flag is set, the hook bypasses the cache entirely
/// to let the canonical recorded entry surface (spec PSQ-2 / Â§6.G S3).
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheHook : IHook
{
    readonly ISemanticCache _cache;

    /// <summary>Creates a new hook backed by <paramref name="cache"/>.</summary>
    public SemanticCacheHook(ISemanticCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <inheritdoc/>
    // After PiiRedactionHook (50), so its before step still publishes the redacted inputs on a hit and
    // the audit entry holds them; its after step then runs after this one and cannot reach the stored outputs.
    // After the replay-mode AuditHook (51), which sets the replaying flag this hook checks.
    public int Order => 52;

    /// <inheritdoc/>
    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (IsReplaying(ctx))
            return ValueTask.CompletedTask;

        var key = SemanticCacheKey.Compute(ctx.ModuleName, ctx.Inputs);
        var entry = _cache.TryGet(key);
        if (entry is null)
            return ValueTask.CompletedTask;

        // Restore audit-determinism metadata so AuditHook.OnAfter records the same content.
        ctx.Set(AuditMetadataKeys.ModelId, entry.ModelId);
        ctx.Set(HookMetadataKeys.PromptTokens, entry.PromptTokens);
        ctx.Set(HookMetadataKeys.CompletionTokens, entry.CompletionTokens);
        ctx.Set(HookMetadataKeys.SemanticCacheHit, true);
        ctx.Outputs = entry.Outputs;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (IsReplaying(ctx)) return ValueTask.CompletedTask;
        // Skip storing on hit-served calls so we don't churn LRU order on identical replays.
        if (ctx.Metadata.TryGetValue(HookMetadataKeys.SemanticCacheHit, out var hit) && hit is true)
            return ValueTask.CompletedTask;
        if (ctx.Outputs is null) return ValueTask.CompletedTask;

        var modelId = ctx.Metadata.TryGetValue(AuditMetadataKeys.ModelId, out var m) && m is string s
            ? s : "unknown";
        var prompt = ctx.Metadata.TryGetValue(HookMetadataKeys.PromptTokens, out var p) && p is long pl
            ? pl : 0L;
        var completion = ctx.Metadata.TryGetValue(HookMetadataKeys.CompletionTokens, out var c) && c is long cl
            ? cl : 0L;

        var key = SemanticCacheKey.Compute(ctx.ModuleName, ctx.Inputs);
        _cache.Set(key, new SemanticCacheEntry(ctx.Outputs, modelId, prompt, completion));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        => ValueTask.CompletedTask;

    static bool IsReplaying(HookContext ctx)
        => ctx.Metadata.TryGetValue(HookMetadataKeys.AuditReplaying, out var v) && v is true;
}
