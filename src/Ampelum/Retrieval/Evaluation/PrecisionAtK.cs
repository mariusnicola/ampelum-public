using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Precision@k: fraction of top-k retrieved IDs that are in the gold set.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PrecisionAtK : IRetrievalMetric
{
    readonly int _defaultK;

    /// <summary>Initialises a Precision@k metric with the given default k.</summary>
    /// <param name="defaultK">Default cutoff (must be positive).</param>
    public PrecisionAtK(int defaultK)
    {
        if (defaultK <= 0) throw new ArgumentOutOfRangeException(nameof(defaultK));
        _defaultK = defaultK;
    }

    /// <inheritdoc/>
    public string Name => $"precision@{_defaultK}";

    /// <inheritdoc/>
    public double Compute(IReadOnlyList<string> retrievedIds, IReadOnlyList<string> goldIds, int? k = null)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(goldIds);
        var kk = k ?? _defaultK;
        var top = retrievedIds.Take(kk).ToList();
        if (top.Count == 0) return 0.0;
        var goldSet = goldIds.ToHashSet(StringComparer.Ordinal);
        var hits = 0;
        foreach (var r in top) if (goldSet.Contains(r)) hits++;
        return (double)hits / top.Count;
    }
}
