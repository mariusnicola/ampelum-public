using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Recall@k: fraction of gold IDs found in the top-k retrieved IDs.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RecallAtK : IRetrievalMetric
{
    readonly int _defaultK;

    /// <summary>Initialises a Recall@k metric with the given default k.</summary>
    /// <param name="defaultK">Default cutoff (must be positive).</param>
    public RecallAtK(int defaultK)
    {
        if (defaultK <= 0) throw new ArgumentOutOfRangeException(nameof(defaultK));
        _defaultK = defaultK;
    }

    /// <inheritdoc/>
    public string Name => $"recall@{_defaultK}";

    /// <inheritdoc/>
    public double Compute(IReadOnlyList<string> retrievedIds, IReadOnlyList<string> goldIds, int? k = null)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(goldIds);
        if (goldIds.Count == 0) return 1.0;
        var kk = k ?? _defaultK;
        var top = retrievedIds.Take(kk).ToHashSet(StringComparer.Ordinal);
        var hits = 0;
        foreach (var g in goldIds) if (top.Contains(g)) hits++;
        return (double)hits / goldIds.Count;
    }
}
