using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Reranks a candidate document set against a query.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IReranker
{
    /// <summary>Returns at most <paramref name="topK"/> documents reordered by reranker score (higher = better).</summary>
    Task<IReadOnlyList<RetrievedDocument>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedDocument> candidates,
        int topK,
        CancellationToken ct = default);
}
