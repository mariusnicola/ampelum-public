using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Combines results from multiple inner retrievers via an <see cref="IReranker"/> (typically RRF).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HybridRetriever(
    IReadOnlyList<IRetriever> sources,
    IReranker reranker,
    HybridRetrieverOptions options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.query");
        activity?.SetTag("SignalGrid.Ampelum.retriever.kind", "hybrid");

        var perSource = await Task.WhenAll(
            sources.Select(s => s.RetrieveAsync(query, options.TopKPerSource, filters, ct))).ConfigureAwait(false);

        if (reranker is ReciprocalRankFusionReranker)
        {
            return ReciprocalRankFusionReranker.Fuse(perSource, k: 60, topK);
        }

        var pool = perSource.SelectMany(x => x)
            .GroupBy(d => d.Id)
            .Select(g => g.First())
            .ToList();
        return await reranker.RerankAsync(query, pool, topK, ct).ConfigureAwait(false);
    }
}
