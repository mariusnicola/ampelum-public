using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Binary NDCG@k: DCG with relâˆˆ{0,1}, normalised by ideal DCG (all gold at top). Score in [0,1].</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class NDCGAtK : IRetrievalMetric
{
    readonly int _defaultK;

    /// <summary>Initialises an NDCG@k metric with the given default k.</summary>
    /// <param name="defaultK">Default cutoff (must be positive).</param>
    public NDCGAtK(int defaultK)
    {
        if (defaultK <= 0) throw new ArgumentOutOfRangeException(nameof(defaultK));
        _defaultK = defaultK;
    }

    /// <inheritdoc/>
    public string Name => $"ndcg@{_defaultK}";

    /// <inheritdoc/>
    public double Compute(IReadOnlyList<string> retrievedIds, IReadOnlyList<string> goldIds, int? k = null)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(goldIds);
        if (goldIds.Count == 0) return 1.0;
        var kk = k ?? _defaultK;
        var goldSet = goldIds.ToHashSet(StringComparer.Ordinal);
        double dcg = 0;
        for (var i = 0; i < retrievedIds.Count && i < kk; i++)
            if (goldSet.Contains(retrievedIds[i])) dcg += 1.0 / Math.Log2(i + 2);
        double idcg = 0;
        var ideal = Math.Min(goldIds.Count, kk);
        for (var i = 0; i < ideal; i++) idcg += 1.0 / Math.Log2(i + 2);
        return idcg == 0 ? 0 : dcg / idcg;
    }
}
