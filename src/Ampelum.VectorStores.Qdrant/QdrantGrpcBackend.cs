using System.Diagnostics.CodeAnalysis;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace SignalGrid.Ampelum.VectorStores.Qdrant;

/// <summary>Default <see cref="IQdrantBackend"/> wrapping a real <see cref="QdrantClient"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class QdrantGrpcBackend : IQdrantBackend
{
    private readonly QdrantClient _client;

    public QdrantGrpcBackend(QdrantOptions options)
    {
        _client = new QdrantClient(options.Host, options.Port, https: false, apiKey: options.ApiKey);
    }

    public async Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct)
    {
        var exists = await _client.CollectionExistsAsync(collection, cancellationToken: ct).ConfigureAwait(false);
        if (!exists)
        {
            await _client.CreateCollectionAsync(
                collection,
                new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
                cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Payload)> points, CancellationToken ct)
    {
        var qpoints = new List<PointStruct>(points.Count);
        foreach (var (id, vec, payload) in points)
        {
            var p = new PointStruct
            {
                Id = ToPointId(id),
                Vectors = vec.ToArray(),
            };
            foreach (var kv in payload)
            {
                p.Payload[kv.Key] = kv.Value;
            }
            qpoints.Add(p);
        }
        await _client.UpsertAsync(collection, qpoints, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Payload)>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct)
    {
        Filter? filter = null;
        if (filterEquals is not null && filterEquals.Count > 0)
        {
            filter = new Filter();
            foreach (var f in filterEquals)
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = f.Key,
                        Match = new Match { Keyword = f.Value },
                    },
                });
            }
        }
        var results = await _client.SearchAsync(
            collection,
            queryVector,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: ct).ConfigureAwait(false);

        var output = new List<(string, double, IReadOnlyDictionary<string, string>)>(results.Count);
        foreach (var r in results)
        {
            var meta = new Dictionary<string, string>();
            foreach (var kv in r.Payload)
            {
                meta[kv.Key] = kv.Value.StringValue;
            }
            output.Add((r.Id.Uuid, r.Score, meta));
        }
        return output;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static PointId ToPointId(string id)
    {
        if (Guid.TryParse(id, out var g))
        {
            return new PointId { Uuid = g.ToString() };
        }
        // Hash arbitrary string into a stable UUID.
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return new PointId { Uuid = new Guid(hash).ToString() };
    }
}
