using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>Options for <see cref="RagAdvisor"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RagAdvisorOptions
{
    /// <summary>Documents fetched from the inner retriever per query. Default 5.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>Separator inserted between concatenated context documents.</summary>
    public string ContextSeparator { get; init; } = "\n---\n";
}
