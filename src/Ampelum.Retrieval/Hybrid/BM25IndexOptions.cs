using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Options for <see cref="BM25Retriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class BM25IndexOptions
{
    /// <summary>Filesystem index directory; null => RAMDirectory.</summary>
    public string? IndexDirectory { get; init; }

    /// <summary>BM25 k1 (term-frequency saturation). Default 1.2.</summary>
    public float K1 { get; init; } = 1.2f;

    /// <summary>BM25 b (length normalization). Default 0.75.</summary>
    public float B { get; init; } = 0.75f;
}
