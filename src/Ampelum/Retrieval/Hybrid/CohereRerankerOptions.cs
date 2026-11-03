using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Options for <see cref="CohereReranker"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class CohereRerankerOptions
{
    /// <summary>Cohere API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Cohere rerank model id. Required; the library has no default model.</summary>
    public required string Model { get; init; }

    /// <summary>Cohere rerank endpoint. Default https://api.cohere.com/v1/rerank.</summary>
    public Uri Endpoint { get; init; } = new("https://api.cohere.com/v1/rerank");
}
