using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.VectorStores.Qdrant;

/// <summary>Options for <see cref="QdrantVectorStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class QdrantOptions
{
    /// <summary>Collection name.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Vector dimensionality.</summary>
    public required int VectorDimension { get; init; }

    /// <summary>Qdrant host. Default localhost.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Qdrant gRPC port. Default 6334.</summary>
    public int Port { get; init; } = 6334;

    /// <summary>Optional API key.</summary>
    public string? ApiKey { get; init; }
}
