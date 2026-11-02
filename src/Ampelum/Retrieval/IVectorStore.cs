namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// Abstraction over a vector index supporting upsert and nearest-neighbour query.
/// Implementations must be thread-safe.
/// Ampelum defines its own narrow <see cref="IVectorStore"/> rather than depending on
/// <c>Microsoft.Extensions.VectorData</c> â€” see spec Â§6.2.1 for rationale (KISS over generality).
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Inserts or replaces documents by <see cref="RetrievedDocument.Id"/>.
    /// <paramref name="documents"/> and <paramref name="vectors"/> are parallel arrays;
    /// <c>documents[i]</c> maps to <c>vectors[i]</c>.
    /// Throws <see cref="ArgumentException"/> if counts differ.
    /// </summary>
    Task UpsertAsync(
        IReadOnlyList<RetrievedDocument> documents,
        ReadOnlyMemory<float>[] vectors,
        CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="topK"/> results by cosine similarity to <paramref name="queryVector"/>,
    /// optionally filtered by metadata equality. Score range is implementation-defined
    /// (<c>InMemoryVectorStore</c> normalises to [0, 1]).
    /// </summary>
    Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default);
}
