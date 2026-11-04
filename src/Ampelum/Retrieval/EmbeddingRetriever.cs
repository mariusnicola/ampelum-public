using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Retrieval;

/// <summary>
/// An <see cref="IRetriever"/> that embeds the query via <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
/// then delegates to an <see cref="IVectorStore"/> for nearest-neighbour lookup.
/// Emits a <c>ampelum.retriever.query</c> activity spanning the full embed + query path (spec Â§6.1.2, PSQ-4).
/// Non-generic â€” type-parameter on the MEAI interface is hardcoded to <c>string</c> /
/// <c>Embedding&lt;float&gt;</c> per spec Â§6.2.3 (PSQ-2).
/// </summary>
public sealed class EmbeddingRetriever : IRetriever
{
    readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    readonly IVectorStore _vectorStore;

    /// <summary>
    /// Constructs an <see cref="EmbeddingRetriever"/>.
    /// </summary>
    public EmbeddingRetriever(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IVectorStore vectorStore)
    {
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(vectorStore);
        _embeddingGenerator = embeddingGenerator;
        _vectorStore = vectorStore;
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
        activity?.SetTag("retriever.type", nameof(EmbeddingRetriever));
        activity?.SetTag("query.topk", topK);

        // GenerateVectorAsync is a MEAI 10.5.x extension method on IEmbeddingGenerator<string, Embedding<float>>
        // returning ReadOnlyMemory<float> â€” confirmed to compile against Microsoft.Extensions.AI 10.5.0.
        var vector = await _embeddingGenerator.GenerateVectorAsync(query, cancellationToken: ct)
            .ConfigureAwait(false);

        var results = await _vectorStore.QueryAsync(vector, topK, filters, ct)
            .ConfigureAwait(false);

        sw.Stop();
        var latency = sw.Elapsed.TotalMilliseconds;
        activity?.SetTag("query.latency_ms", latency);
        activity?.SetTag("query.hit_count", (long)results.Count);

        Diagnostics.RetrieverQueryDuration.Record(latency,
            new KeyValuePair<string, object?>("retriever.type", nameof(EmbeddingRetriever)));
        Diagnostics.RetrieverQueryHits.Record(results.Count,
            new KeyValuePair<string, object?>("retriever.type", nameof(EmbeddingRetriever)));

        // The store's score is the only similarity there is; the document carries the score it was indexed with.
        return results.Select(r => r.Document with { Score = r.Score }).ToList();
    }
}
