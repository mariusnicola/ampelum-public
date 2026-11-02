using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Caching.Semantic;

/// <summary>
/// Sliding-window cache keyed by a caller-supplied canonical request key. Implementations
/// MUST be thread-safe (PSQ-2, v0.9g). The cache stores opaque <see cref="SemanticCacheEntry"/>
/// records; the <see cref="SemanticCacheHook"/> populates them with the parsed module outputs
/// plus the audit-determinism metadata (model id, token counts) so that a hit produces an
/// audit chain entry with content semantically identical to the original miss.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface ISemanticCache
{
    /// <summary>Attempts to fetch the entry stored under <paramref name="key"/>.</summary>
    /// <param name="key">Canonical request key.</param>
    /// <returns>The cached entry, or <see langword="null"/> on miss.</returns>
    SemanticCacheEntry? TryGet(string key);

    /// <summary>Stores <paramref name="entry"/> under <paramref name="key"/>, evicting the
    /// least-recently-used entry when the configured capacity is reached.</summary>
    void Set(string key, SemanticCacheEntry entry);

    /// <summary>Current entry count. Useful for tests and observability.</summary>
    int Count { get; }
}
