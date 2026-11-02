using System.Text.Json;
using SignalGrid.Ampelum.VectorStores.PgVector;
using SignalGrid.Ampelum.VectorStores.Pinecone;
using SignalGrid.Ampelum.VectorStores.Qdrant;

namespace Ampelum.Samples.VectorStoreSwap;

/// <summary>One row as a backend received it. Fields are named as that store names them.</summary>
public sealed record StoredRow(string Id, IReadOnlyDictionary<string, string> Fields);

/// <summary>One call a backend received. Members a call does not use stay at their defaults.</summary>
public sealed record BackendRequest(
    string Operation,
    string Target,
    int Dimension = 0,
    int TopK = 0,
    IReadOnlyDictionary<string, string>? Filter = null,
    IReadOnlyList<StoredRow>? Rows = null);

/// <summary>
/// Cosine ranking shared by the three fake backends. The real services rank with the same measure,
/// but these fakes exercise only the adapter code in front of them, not the databases.
/// </summary>
sealed class FakePointIndex
{
    readonly Dictionary<string, (float[] Vector, IReadOnlyDictionary<string, string> Fields)> _points = new();

    public void Upsert(string id, ReadOnlyMemory<float> vector, IReadOnlyDictionary<string, string> fields) =>
        _points[id] = (vector.ToArray(), fields);

    public List<(string Id, double Score, IReadOnlyDictionary<string, string> Fields)> Search(
        ReadOnlyMemory<float> query, int topK, IReadOnlyDictionary<string, string>? filter) =>
        _points
            .Where(p => filter is null || filter.All(f => p.Value.Fields.TryGetValue(f.Key, out var v) && v == f.Value))
            .Select(p => (p.Key, Cosine(query.Span, p.Value.Vector), p.Value.Fields))
            .OrderByDescending(h => h.Item2)
            .Take(topK)
            .ToList();

    static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
        return denominator < 1e-12 ? 0 : dot / denominator;
    }
}

public sealed class FakePgVectorBackend : IPgVectorBackend
{
    readonly FakePointIndex _index = new();
    readonly Dictionary<string, (string Text, string MetadataJson)> _rows = new();

    public List<BackendRequest> Requests { get; } = [];

    public Task EnsureSchemaAsync(string table, int dim, CancellationToken ct)
    {
        Requests.Add(new BackendRequest("ensure-schema", table, Dimension: dim));
        return Task.CompletedTask;
    }

    public Task UpsertAsync(
        string table,
        IReadOnlyList<(string Id, string Text, string MetadataJson, ReadOnlyMemory<float> Embedding)> rows,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("upsert", table, Rows: rows
            .Select(r => new StoredRow(r.Id, new Dictionary<string, string> { ["text"] = r.Text, ["metadata"] = r.MetadataJson }))
            .ToList()));
        foreach (var (id, text, json, embedding) in rows)
        {
            _rows[id] = (text, json);
            _index.Upsert(id, embedding, ParseMetadata(json));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Id, string Text, string MetadataJson, double Score)>> QueryAsync(
        string table,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("query", table, TopK: topK, Filter: filterEquals));
        IReadOnlyList<(string, string, string, double)> hits = _index.Search(queryVector, topK, filterEquals)
            .Select(h => (h.Id, _rows[h.Id].Text, _rows[h.Id].MetadataJson, h.Score))
            .ToList();
        return Task.FromResult(hits);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The real query compares the metadata column as text, so every value is read as a string.
    static Dictionary<string, string> ParseMetadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
}

public sealed class FakePineconeIndex : IPineconeIndex
{
    readonly FakePointIndex _index = new();

    public List<BackendRequest> Requests { get; } = [];

    public Task UpsertAsync(
        string @namespace,
        IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Metadata)> points,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("upsert", @namespace, Rows: points.Select(p => new StoredRow(p.Id, p.Metadata)).ToList()));
        foreach (var (id, vector, metadata) in points)
            _index.Upsert(id, vector, metadata);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Metadata)>> QueryAsync(
        string @namespace,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("query", @namespace, TopK: topK, Filter: filterEquals));
        IReadOnlyList<(string, double, IReadOnlyDictionary<string, string>)> hits = _index.Search(queryVector, topK, filterEquals);
        return Task.FromResult(hits);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeQdrantBackend : IQdrantBackend
{
    readonly FakePointIndex _index = new();

    public List<BackendRequest> Requests { get; } = [];

    public Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct)
    {
        Requests.Add(new BackendRequest("ensure-collection", collection, Dimension: dimension));
        return Task.CompletedTask;
    }

    public Task UpsertAsync(
        string collection,
        IReadOnlyList<(string Id, ReadOnlyMemory<float> Vector, IReadOnlyDictionary<string, string> Payload)> points,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("upsert", collection, Rows: points.Select(p => new StoredRow(p.Id, p.Payload)).ToList()));
        foreach (var (id, vector, payload) in points)
            _index.Upsert(ToPointUuid(id), vector, payload);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(string Id, double Score, IReadOnlyDictionary<string, string> Payload)>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct)
    {
        Requests.Add(new BackendRequest("search", collection, TopK: topK, Filter: filterEquals));
        // The ids that come back are the stored point uuids, as in the real backend.
        IReadOnlyList<(string, double, IReadOnlyDictionary<string, string>)> hits = _index.Search(queryVector, topK, filterEquals);
        return Task.FromResult(hits);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Same mapping as the real backend: anything <see cref="Guid.TryParse(string?, out Guid)"/> accepts
    /// becomes the canonical lowercase hyphenated form, every other id becomes a UUID built from its MD5 hash.
    /// </summary>
    public static string ToPointUuid(string id)
    {
        if (Guid.TryParse(id, out var guid))
            return guid.ToString();
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        return new Guid(hash).ToString();
    }
}
