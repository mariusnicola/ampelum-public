using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Retrieval;

namespace SignalGrid.Ampelum.VectorStores.Qdrant;

/// <summary>Abstraction over Qdrant gRPC operations used by <see cref="QdrantVectorStore"/> (test seam).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IQdrantBackend : IAsyncDisposable
{
    /// <summary>Ensures the collection exists with the given vector dimension.</summary>
    Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct);

    /// <summary>Upserts (id, vector, payload) tuples.</summary>
    Task UpsertAsync(string collection, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Payload)> points, CancellationToken ct);

    /// <summary>Cosine-similarity search; returns (id, score, payload) ranked.</summary>
    Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Payload)>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct);
}

/// <summary>Qdrant adapter for <see cref="IVectorStore"/>. Stores text under payload key <c>"__text"</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class QdrantVectorStore : IVectorStore, IAsyncDisposable
{
    private const string TextKey = "__text";

    private readonly IQdrantBackend _backend;
    private readonly QdrantOptions _options;
    private bool _ensured;

    /// <summary>Production constructor — wires a real Qdrant gRPC client to <paramref name="options"/>.</summary>
    public QdrantVectorStore(QdrantOptions options)
        : this(options, new QdrantGrpcBackend(options))
    {
    }

    /// <summary>Test-seam constructor.</summary>
    public QdrantVectorStore(QdrantOptions options, IQdrantBackend backend)
    {
        _options = options;
        _backend = backend;
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
        await EnsureAsync(ct).ConfigureAwait(false);
        var points = new List<(string, ReadOnlyMemory<float>, IReadOnlyDictionary<string, string>)>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            var payload = new Dictionary<string, string> { [TextKey] = documents[i].Text };
            foreach (var kv in documents[i].Metadata)
            {
                payload[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }
            points.Add((documents[i].Id, vectors[i], payload));
        }
        await _backend.UpsertAsync(_options.CollectionName, points, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<string, string>? filterEquals = null;
        if (filters is not null && filters.Count > 0)
        {
            var dict = new Dictionary<string, string>(filters.Count);
            foreach (var f in filters)
            {
                dict[f.Key] = f.Value?.ToString() ?? string.Empty;
            }
            filterEquals = dict;
        }

        var hits = await _backend.SearchAsync(_options.CollectionName, queryVector, topK, filterEquals, ct).ConfigureAwait(false);
        var output = new List<(RetrievedDocument, double)>(hits.Count);
        foreach (var (id, score, payload) in hits)
        {
            string text = string.Empty;
            var meta = new Dictionary<string, object?>();
            foreach (var kv in payload)
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

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ensured)
        {
            return;
        }
        await _backend.EnsureCollectionAsync(_options.CollectionName, _options.VectorDimension, ct).ConfigureAwait(false);
        _ensured = true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}
