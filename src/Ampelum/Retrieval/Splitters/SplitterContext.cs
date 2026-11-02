using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Splitters;

/// <summary>Per-call splitter context.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SplitterContext
{
    /// <summary>Maximum chunk size (characters or tokens depending on splitter). Default 1000.</summary>
    public int ChunkSize { get; init; } = 1000;

    /// <summary>Inter-chunk overlap. Default 200.</summary>
    public int ChunkOverlap { get; init; } = 200;

    /// <summary>Source document id used for chunk-id derivation. Default "doc".</summary>
    public string SourceDocumentId { get; init; } = "doc";
}
