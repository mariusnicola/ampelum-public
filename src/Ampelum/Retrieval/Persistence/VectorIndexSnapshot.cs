using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Persistence;

/// <summary>Serializable snapshot of an in-memory vector index.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record VectorIndexSnapshot(
    IReadOnlyList<RetrievedDocument> Documents,
    IReadOnlyList<float[]> Vectors);
