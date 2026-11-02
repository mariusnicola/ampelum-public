using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Evaluation;

/// <summary>Retrieval evaluation metric. Concrete implementations (NDCG@k, MRR, recall@k, precision@k) ship in v0.9d.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IRetrievalMetric
{
    /// <summary>Metric short name (e.g. "ndcg@5"). Used as result-dictionary key.</summary>
    string Name { get; }

    /// <summary>Computes a single score from retrieved IDs vs. gold IDs (and an optional cutoff <paramref name="k"/>).</summary>
    double Compute(IReadOnlyList<string> retrievedIds, IReadOnlyList<string> goldIds, int? k = null);
}
