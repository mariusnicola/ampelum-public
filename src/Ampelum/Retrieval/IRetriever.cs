namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// A document returned by an <see cref="IRetriever"/> query.
/// <see cref="Score"/> semantics: higher is better. Range depends on the implementation
/// (BM25: unbounded; cosine similarity in <c>InMemoryVectorStore</c>: [0, 1]).
/// </summary>
public sealed record RetrievedDocument(
    string Id,
    string Text,
    double Score,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>
/// Retrieves documents relevant to a query.
/// Implementations are side-effectful (network or disk I/O) and SHOULD emit a
/// <c>ampelum.retriever.query</c> activity on <see cref="Diagnostics.Source"/>
/// (see <c>RetrieverInstrumentation</c> for easy compliance).
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Returns up to <paramref name="topK"/> documents ranked by relevance to <paramref name="query"/>.
    /// <paramref name="filters"/> are passed through to the underlying store; semantics are implementation-defined.
    /// </summary>
    Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default);
}
