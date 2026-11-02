using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Helpers to evaluate an <see cref="IRetriever"/> against a labelled dataset using <see cref="IRetrievalMetric"/>s.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class RetrievalEvaluationExtensions
{
    /// <summary>Runs the retriever over each (query, goldIds) pair, applies each metric, returns the mean per metric.</summary>
    public static async Task<IReadOnlyDictionary<string, double>> EvaluateRetrievalAsync(
        this IRetriever retriever,
        IReadOnlyList<(string Query, IReadOnlyList<string> GoldIds)> dataset,
        IReadOnlyList<IRetrievalMetric> metrics,
        int topK,
        CancellationToken ct = default)
    {
        var totals = metrics.ToDictionary(m => m.Name, _ => 0.0);
        foreach (var (q, gold) in dataset)
        {
            var docs = await retriever.RetrieveAsync(q, topK, ct: ct).ConfigureAwait(false);
            var ids = docs.Select(d => d.Id).ToList();
            foreach (var m in metrics)
            {
                totals[m.Name] += m.Compute(ids, gold, topK);
            }
        }
        return totals.ToDictionary(kv => kv.Key, kv => kv.Value / dataset.Count);
    }
}
