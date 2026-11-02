using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Retrieval;

namespace SignalGrid.Ampelum.VectorStores.Pinecone;

/// <summary>Abstraction over Pinecone index operations used by <see cref="PineconeVectorStore"/> (test seam).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IPineconeIndex : IAsyncDisposable
{
    /// <summary>Upserts (id, vector, metadata) tuples.</summary>
    Task UpsertAsync(string @namespace, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Metadata)> points, CancellationToken ct);

    /// <summary>Returns top-k matches for the query vector.</summary>
    Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Metadata)>> QueryAsync(
        string @namespace,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct);
}

/// <summary>Pinecone adapter for <see cref="IVectorStore"/>. Stores text under metadata key <c>"__text"</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PineconeVectorStore : IVectorStore, IAsyncDisposable
{
    private const string TextKey = "__text";

    private readonly PineconeOptions _options;
    private readonly IPineconeIndex _index;

    /// <summary>Production constructor — instantiates a real Pinecone client.</summary>
    public PineconeVectorStore(PineconeOptions options)
        : this(options, new PineconeClientAdapter(options))
    {
    }

    /// <summary>Test-seam constructor.</summary>
    public PineconeVectorStore(PineconeOptions options, IPineconeIndex index)
    {
        _options = options;
        _index = index;
    }

    /// <inheritdoc />
    public async Task UpsertAsync(IReadOnlyList<RetrievedDocument> documents, ReadOnlyMemory<float>[] vectors, CancellationToken ct = default)
    {
        if (documents.Count != vectors.Length)
        {
            throw new ArgumentException(
                $"documents.Count ({documents.Count}) must equal vectors.Length ({vectors.Length}).",
                nameof(vectors));
        }
        var points = new List<(string, ReadOnlyMemory<float>, IReadOnlyDictionary<string, string>)>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            var meta = new Dictionary<string, string> { [TextKey] = documents[i].Text };
            foreach (var kv in documents[i].Metadata)
            {
                meta[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
            points.Add((documents[i].Id, vectors[i], meta));
        }
        await _index.UpsertAsync(_options.Namespace, points, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string>? eq = null;
        if (filters is not null && filters.Count > 0)
        {
            var d = new Dictionary<string, string>(filters.Count);
            foreach (var f in filters)
            {
                d[f.Key] = f.Value?.ToString() ?? string.Empty;
            }
            eq = d;
        }
        var hits = await _index.QueryAsync(_options.Namespace, queryVector, topK, eq, ct).ConfigureAwait(false);
        var output = new List<(RetrievedDocument, double)>(hits.Count);
        foreach (var (id, score, metaIn) in hits)
        {
            string text = string.Empty;
            var meta = new Dictionary<string, object?>();
            foreach (var kv in metaIn)
            {
                if (kv.Key == TextKey)
                {
                    text = kv.Value;
                }
                else
                {
                    meta[kv.Key] = kv.Value;
                }
            }
            output.Add((new RetrievedDocument(id, text, score, meta), score));
        }
        return output;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _index.DisposeAsync();
}
