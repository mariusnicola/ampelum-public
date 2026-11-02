using System.Diagnostics.CodeAnalysis;
using Pinecone;

namespace SignalGrid.Ampelum.VectorStores.Pinecone;

/// <summary>Default <see cref="IPineconeIndex"/> wrapping the real <see cref="PineconeClient"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class PineconeClientAdapter : IPineconeIndex
{
    private readonly PineconeClient _client;
    private readonly string _indexName;
    private Index<global::Pinecone.Rest.RestTransport>? _index;

    public PineconeClientAdapter(PineconeOptions options)
    {
        _client = new PineconeClient(options.ApiKey);
        _indexName = options.IndexName;
    }

    private async Task<Index<global::Pinecone.Rest.RestTransport>> EnsureIndexAsync(CancellationToken ct)
    {
        return _index ??= await _client.GetIndex(_indexName, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(string @namespace, IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Metadata)> points, CancellationToken ct)
    {
        var idx = await EnsureIndexAsync(ct).ConfigureAwait(false);
        var vecs = new List<Vector>(points.Count);
        foreach (var (id, vec, meta) in points)
        {
            var md = new MetadataMap();
            foreach (var kv in meta)
            {
                md[kv.Key] = kv.Value;
            }
            vecs.Add(new Vector { Id = id, Values = vec.ToArray(), Metadata = md });
        }
        await idx.Upsert(vecs, indexNamespace: @namespace, ct: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Metadata)>> QueryAsync(string @namespace, ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, string>? filterEquals, CancellationToken ct)
    {
        var idx = await EnsureIndexAsync(ct).ConfigureAwait(false);
        MetadataMap? filter = null;
        if (filterEquals is not null)
        {
            filter = new MetadataMap();
            foreach (var f in filterEquals)
            {
                filter[f.Key] = f.Value;
            }
        }
        var results = await idx.Query(queryVector.ToArray(), (uint)topK, filter: filter, indexNamespace: @namespace, includeMetadata: true, ct: ct).ConfigureAwait(false);
        var output = new List<(string, double, IReadOnlyDictionary<string, string>)>(results.Length);
        foreach (var r in results)
        {
            var meta = new Dictionary<string, string>();
            if (r.Metadata is not null)
            {
                foreach (var kv in r.Metadata)
                {
                    meta[kv.Key] = kv.Value.Inner?.ToString() ?? string.Empty;
                }
            }
            output.Add((r.Id, r.Score, meta));
        }
        return output;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
