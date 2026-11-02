using System.Diagnostics;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// Transparent <see cref="IRetriever"/> decorator that adds observability to any inner retriever.
/// Wraps the <c>RetrieveAsync</c> call with a <c>ampelum.retriever.query</c> activity on
/// <see cref="Diagnostics.Source"/> and records duration + hit-count on the meter instruments.
/// Use this to instrument third-party or user-supplied <see cref="IRetriever"/> implementations
/// that do not emit the activity themselves. Usage: <c>new RetrieverInstrumentation(myRetriever)</c>.
/// </summary>
public sealed class RetrieverInstrumentation : IRetriever
{
    readonly IRetriever _inner;

    /// <summary>Constructs a <see cref="RetrieverInstrumentation"/> wrapping <paramref name="innerRetriever"/>.</summary>
    public RetrieverInstrumentation(IRetriever innerRetriever)
    {
        ArgumentNullException.ThrowIfNull(innerRetriever);
        _inner = innerRetriever;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
        string query,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.retriever.query");
        activity?.SetTag("retriever.type", _inner.GetType().Name);
        activity?.SetTag("query.topk", topK);

        var results = await _inner.RetrieveAsync(query, topK, filters, ct).ConfigureAwait(false);

        sw.Stop();
        var latency = sw.Elapsed.TotalMilliseconds;
        activity?.SetTag("query.latency_ms", latency);
        activity?.SetTag("query.hit_count", (long)results.Count);

        Diagnostics.RetrieverQueryDuration.Record(latency,
            new KeyValuePair<string, object?>("retriever.type", _inner.GetType().Name));
        Diagnostics.RetrieverQueryHits.Record(results.Count,
            new KeyValuePair<string, object?>("retriever.type", _inner.GetType().Name));

        return results;
    }
}
