using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Retrieval.Filtering;

/// <summary>Decorates an <see cref="IRetriever"/> dropping documents whose <see cref="RetrievedDocument.Score"/> is below <c>minScore</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ScoreThresholdRetriever(IRetriever inner, double minScore) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        var raw = await inner.RetrieveAsync(query, topK, filters, ct).ConfigureAwait(false);
        var result = new List<RetrievedDocument>(raw.Count);
        foreach (var d in raw)
        {
            if (d.Score >= minScore)
            {
                result.Add(d);
            }
        }
        return result;
    }
}
