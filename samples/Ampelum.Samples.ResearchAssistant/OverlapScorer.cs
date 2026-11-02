using SignalGrid.Ampelum.Retrieval.Reranking;

namespace Ampelum.Samples.ResearchAssistant;

/// <summary>
/// Relevance scorer for the cross-encoder reranker that needs no ONNX model: the share of query terms
/// found in the passage, plus a bonus for query word pairs that appear side by side.
/// </summary>
public sealed class OverlapScorer : IRelevanceScorer
{
    public float Score(string query, string document)
    {
        var q = TermText.Terms(query);
        if (q.Count == 0)
            return 0f;
        var d = TermText.Terms(document);
        var dSet = d.ToHashSet(StringComparer.Ordinal);
        var distinct = q.Distinct().ToList();
        var coverage = distinct.Count(dSet.Contains) / (float)distinct.Count;

        var pairs = 0;
        var hits = 0;
        for (var i = 0; i + 1 < q.Count; i++)
        {
            pairs++;
            for (var j = 0; j + 1 < d.Count; j++)
            {
                if (d[j] == q[i] && d[j + 1] == q[i + 1])
                {
                    hits++;
                    break;
                }
            }
        }
        return coverage + (pairs == 0 ? 0f : 0.5f * hits / pairs);
    }

    public void Dispose() { }
}
