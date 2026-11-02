using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.VectorStores.Pinecone;

/// <summary>Options for <see cref="PineconeVectorStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PineconeOptions
{
    /// <summary>Pinecone API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Pinecone environment / project URL fragment.</summary>
    public required string Environment { get; init; }

    /// <summary>Index name.</summary>
    public required string IndexName { get; init; }

    /// <summary>Optional namespace within the index.</summary>
    public string Namespace { get; init; } = "";
}
