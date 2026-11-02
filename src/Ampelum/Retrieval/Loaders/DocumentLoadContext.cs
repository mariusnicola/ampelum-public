using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Loaders;

/// <summary>Per-call loader context (source URI, encoding, byte budget).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class DocumentLoadContext
{
    /// <summary>Optional source URI for metadata + chunk-id derivation.</summary>
    public string? SourceUri { get; init; }

    /// <summary>Default encoding for text loaders that need one. Default UTF-8.</summary>
    public System.Text.Encoding Encoding { get; init; } = System.Text.Encoding.UTF8;

    /// <summary>Maximum permitted document byte budget; null = unbounded. Throws <see cref="LoaderBudgetExceededException"/> when exceeded.</summary>
    public long? MaxDocumentBytes { get; init; }
}
