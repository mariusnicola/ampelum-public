using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Hybrid;

/// <summary>Reciprocal Rank Fusion reranker (Cormack et al. 2009). LM-free fusion across ranked lists.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ReciprocalRankFusionReranker(int k = 60) : IReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievedDocument>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedDocument> candidates,
        int topK,
        CancellationToken ct = default)
        => Task.FromResult(Fuse(new[] { candidates }, k, topK));

    /// <summary>Fuses multiple ranked lists into a single list using RRF: score(d) = Î£ 1/(k + rank_i(d) + 1).</summary>
    public static IReadOnlyList<RetrievedDocument> Fuse(
        IReadOnlyList<IReadOnlyList<RetrievedDocument>> lists,
        int k,
        int topK)
    {
        var scores = new Dictionary<string, double>();
        var firstSeen = new Dictionary<string, RetrievedDocument>();
        foreach (var list in lists)
        {
            for (int rank = 0; rank < list.Count; rank++)
            {
                var d = list[rank];
                var contribution = 1.0 / (k + rank + 1);
                scores[d.Id] = scores.GetValueOrDefault(d.Id) + contribution;
                firstSeen.TryAdd(d.Id, d);
            }
        }
        return scores.OrderByDescending(kv => kv.Value).Take(topK)
            .Select(kv => firstSeen[kv.Key] with { Score = kv.Value }).ToList();
    }
}
