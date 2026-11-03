using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Options for <see cref="VoyageReranker"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class VoyageRerankerOptions
{
    /// <summary>Voyage AI API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Voyage rerank model id. Required; the library has no default model.</summary>
    public required string Model { get; init; }

    /// <summary>Voyage rerank endpoint. Default https://api.voyageai.com/v1/rerank.</summary>
    public Uri Endpoint { get; init; } = new("https://api.voyageai.com/v1/rerank");
}
