using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Mean Reciprocal Rank: 1 / (1-indexed position) of the first gold ID found in retrieved; 0 if none.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MRR : IRetrievalMetric
{
    /// <inheritdoc/>
    public string Name => "mrr";

    /// <inheritdoc/>
    public double Compute(IReadOnlyList<string> retrievedIds, IReadOnlyList<string> goldIds, int? k = null)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(goldIds);
        if (goldIds.Count == 0) return 1.0;
        var goldSet = goldIds.ToHashSet(StringComparer.Ordinal);
        var limit = k ?? retrievedIds.Count;
        for (var i = 0; i < retrievedIds.Count && i < limit; i++)
            if (goldSet.Contains(retrievedIds[i])) return 1.0 / (i + 1);
        return 0.0;
    }
}
