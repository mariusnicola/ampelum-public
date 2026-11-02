using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Caching;

/// <summary>Options for <see cref="EmbeddingCache"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class EmbeddingCacheOptions
{
    /// <summary>Cache entry lifetime. Default <see cref="TimeSpan.MaxValue"/> (no expiry â€” embeddings are deterministic per model).</summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.MaxValue;
    /// <summary>Cache key prefix. Default "ampelum:emb:".</summary>
    public string KeyPrefix { get; init; } = "ampelum:emb:";
}
