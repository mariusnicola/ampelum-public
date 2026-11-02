using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Options for <see cref="HybridRetriever"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HybridRetrieverOptions
{
    /// <summary>Per-source candidate pool size (each source returns this many docs before fusion). Default 20.</summary>
    public int TopKPerSource { get; init; } = 20;
}
