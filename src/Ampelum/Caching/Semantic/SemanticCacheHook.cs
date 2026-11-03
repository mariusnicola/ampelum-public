using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Hook that wires an <see cref="ISemanticCache"/> into the forward pipeline. On cache hit
/// (<see cref="OnBeforeAsync"/>) the hook short-circuits the LM call by populating
/// <see cref="HookContext.Outputs"/>, and publishes the model id that produced the stored answer
/// (with zero prompt and completion tokens, since the hit costs nothing) so the downstream
/// <see cref="AuditHook"/> records that model. On miss (<see cref="OnAfterAsync"/>) the hook stores the
/// parsed outputs with the model id (the audit key, else <c>HookMetadataKeys.ModelId</c>) and the tokens used.
/// </summary>
/// <remarks>
/// An entry belongs to the module name, the signature type (<c>HookMetadataKeys.SignatureType</c>, set by
/// <c>Predict</c>) and the inputs. Without an embedding generator only identical inputs hit. With one, a
/// lookup that misses exactly embeds the inputs and takes the most similar stored entry of the same module
/// and signature whose cosine similarity reaches the threshold, and records it in
/// <c>HookMetadataKeys.SemanticCacheSimilarity</c>. The hook keeps the vectors itself (at most 4096, oldest
/// dropped first). The embedding service only speeds lookups up: an embedding call that throws is skipped
/// (the exact entry is still stored, the lookup falls back to the exact match) and does not fail the model
/// call; cancellation still propagates. A streamed turn (<c>HookMetadataKeys.Streamed</c>) is never served,
/// stored or embedded, because its tokens reach the caller before any cached answer could be used. Nothing is
/// stored or embedded either once an after hook has thrown (<c>HookMetadataKeys.AfterHookFailed</c>), so a blocked
/// reply is never served later.
///
/// Order (52) is just after the PII redaction hook (50) and the replay-mode audit hook (51), so on a hit that hook's OnBefore has already
/// published the redacted inputs that <see cref="AuditHook"/> records; hooks ordered above 52 do not
/// run their OnBefore on a hit. When the
/// <c>HookMetadataKeys.AuditReplaying</c> flag is set, the hook bypasses the cache entirely
/// to let the canonical recorded entry surface (spec PSQ-2 / Â§6.G S3).
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheHook : IHook
{
    const int MaxIndexed = 4096;

    sealed record Indexed(string Scope, string Key, float[] Vector);

    readonly ISemanticCache _cache;
    readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    readonly double _minSimilarity;
    readonly object _gate = new();
    readonly List<Indexed> _index = [];

    /// <summary>Creates a new hook backed by <paramref name="cache"/>.</summary>
    /// <param name="cache">The entry store.</param>
    /// <param name="embeddings">When given, a lookup that misses exactly falls back to the most similar stored entry.</param>
    /// <param name="minSimilarity">Cosine similarity, in (0, 1], an entry needs to be served for a different input.</param>
    public SemanticCacheHook(
        ISemanticCache cache,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        double minSimilarity = 0.95)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (minSimilarity is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), minSimilarity, "Must be in (0, 1].");
        _cache = cache;
        _embeddings = embeddings;
        _minSimilarity = minSimilarity;
    }

    static string? SignatureOf(HookContext ctx)
        => ctx.Metadata.TryGetValue(HookMetadataKeys.SignatureType, out var v) ? v as string : null;

    /// <inheritdoc/>
    // After PiiRedactionHook (50), so its before step still publishes the redacted inputs on a hit and
    // the audit entry holds them; its after step then runs after this one and cannot reach the stored outputs.
    // After the replay-mode AuditHook (51), which sets the replaying flag this hook checks.
    public int Order => 52;

    /// <inheritdoc/>
    public async ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (IsReplaying(ctx) || IsStreamed(ctx))
            return;

        var signature = SignatureOf(ctx);
        var key = SemanticCacheKey.Compute(ctx.ModuleName, ctx.Inputs, signature);
        var entry = _cache.TryGet(key);
        if (entry is null && _embeddings is not null)
        {
            var found = await FindSimilarAsync(SemanticCacheKey.Scope(ctx.ModuleName, signature), ctx.Inputs, ct)
                .ConfigureAwait(false);
            if (found is { } f)
            {
                entry = f.Entry;
                ctx.Set(HookMetadataKeys.SemanticCacheSimilarity, f.Similarity);
            }
        }
        if (entry is null)
            return;

        // AuditHook records the model that produced the answer; the hit itself used no tokens.
        ctx.Set(AuditMetadataKeys.ModelId, entry.ModelId);
        ctx.Set(HookMetadataKeys.PromptTokens, 0L);
        ctx.Set(HookMetadataKeys.CompletionTokens, 0L);
        ctx.Set(HookMetadataKeys.SemanticCacheHit, true);
        ctx.Outputs = entry.Outputs;
    }

    async ValueTask<(SemanticCacheEntry Entry, double Similarity)?> FindSimilarAsync(
        string scope, IReadOnlyDictionary<string, object?> inputs, CancellationToken ct)
    {
        Indexed[] candidates;
        lock (_gate) candidates = _index.Where(i => i.Scope == scope).ToArray();
        if (candidates.Length == 0)
            return null;

        // The embedding service is an accelerator: when it fails the lookup is a plain miss.
        var query = await TryEmbedAsync(inputs, ct).ConfigureAwait(false);
        if (query is null)
            return null;
        foreach (var (candidate, similarity) in candidates
            .Select(c => (c, Cosine(query, c.Vector)))
            .Where(t => t.Item2 >= _minSimilarity)
            .OrderByDescending(t => t.Item2))
        {
            var entry = _cache.TryGet(candidate.Key);
            if (entry is not null)
                return (entry, similarity);
            // The store evicted it; the vector is dead weight.
            lock (_gate) _index.Remove(candidate);
        }
        return null;
    }

    // Null when the embedding call failed. The hook has no logger to report it to; cancellation still propagates.
    async ValueTask<float[]?> TryEmbedAsync(IReadOnlyDictionary<string, object?> inputs, CancellationToken ct)
    {
        try
        {
            var generated = await _embeddings!.GenerateAsync(
                [SemanticCacheKey.Text(inputs)], cancellationToken: ct).ConfigureAwait(false);
            return generated[0].Vector.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <inheritdoc/>
    public async ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (IsReplaying(ctx) || IsStreamed(ctx) || AfterHookFailed(ctx)) return;
        // Skip storing on hit-served calls so we don't churn LRU order on identical replays.
        if (ctx.Metadata.TryGetValue(HookMetadataKeys.SemanticCacheHit, out var hit) && hit is true)
            return;
        if (ctx.Outputs is null) return;

        // The same lookup AuditHook uses: its own key first, then the model id the module published.
        var modelId = ctx.Metadata.TryGetValue(AuditMetadataKeys.ModelId, out var m) && m is string s
            ? s
            : ctx.Metadata.TryGetValue(HookMetadataKeys.ModelId, out var hm) && hm is string hs ? hs : "unknown";
        var prompt = ctx.Metadata.TryGetValue(HookMetadataKeys.PromptTokens, out var p) && p is long pl
            ? pl : 0L;
        var completion = ctx.Metadata.TryGetValue(HookMetadataKeys.CompletionTokens, out var c) && c is long cl
            ? cl : 0L;

        var signature = SignatureOf(ctx);
        var key = SemanticCacheKey.Compute(ctx.ModuleName, ctx.Inputs, signature);
        _cache.Set(key, new SemanticCacheEntry(ctx.Outputs, modelId, prompt, completion));

        if (_embeddings is not null)
        {
            var vector = await TryEmbedAsync(ctx.Inputs, ct).ConfigureAwait(false);
            if (vector is null)
                return; // the exact entry is stored; it just has no vector
            lock (_gate)
            {
                _index.RemoveAll(i => i.Key == key);
                _index.Add(new Indexed(SemanticCacheKey.Scope(ctx.ModuleName, signature), key, vector));
                if (_index.Count > MaxIndexed)
                    _index.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        => ValueTask.CompletedTask;

    static bool AfterHookFailed(HookContext ctx)
        => ctx.Metadata.TryGetValue(HookMetadataKeys.AfterHookFailed, out var v) && v is true;

    static bool IsStreamed(HookContext ctx)
        => ctx.Metadata.TryGetValue(HookMetadataKeys.Streamed, out var v) && v is true;

    static bool IsReplaying(HookContext ctx)
        => ctx.Metadata.TryGetValue(HookMetadataKeys.AuditReplaying, out var v) && v is true;
}
