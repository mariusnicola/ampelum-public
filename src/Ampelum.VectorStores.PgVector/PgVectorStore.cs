using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SignalGrid.Ampelum.Retrieval;

namespace SignalGrid.Ampelum.VectorStores.PgVector;

/// <summary>Abstraction over pgvector SQL operations used by <see cref="PgVectorStore"/> (test seam).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IPgVectorBackend : IAsyncDisposable
{
    /// <summary>Ensures the schema (extension + table + index) exists.</summary>
    Task EnsureSchemaAsync(string table, int dim, CancellationToken ct);

    /// <summary>Upserts (id, text, metadataJson, embedding) tuples.</summary>
    Task UpsertAsync(string table, IReadOnlyList<(string Id, string Text, string MetadataJson, ReadOnlyMemory<float> Embedding)> rows, CancellationToken ct);

    /// <summary>Returns top-k matches ordered by cosine distance (closer = better; score = 1 - distance).</summary>
    Task<IReadOnlyList<(string Id, string Text, string MetadataJson, double Score)>> QueryAsync(
        string table,
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, string>? filterEquals,
        CancellationToken ct);
}

/// <summary>pgvector adapter for <see cref="IVectorStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PgVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly PgVectorOptions _options;
    private readonly IPgVectorBackend _backend;
    private bool _ensured;

    /// <summary>Production constructor — opens an Npgsql data source against <see cref="PgVectorOptions.ConnectionString"/>.</summary>
    public PgVectorStore(PgVectorOptions options)
        : this(options, new NpgsqlPgVectorBackend(options))
    {
    }

    /// <summary>Test-seam constructor.</summary>
    public PgVectorStore(PgVectorOptions options, IPgVectorBackend backend)
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
        var rows = new List<(string, string, string, ReadOnlyMemory<float>)>(documents.Count);
        for (int i = 0; i < documents.Count; i++)
        {
            var json = JsonSerializer.Serialize(documents[i].Metadata);
            rows.Add((documents[i].Id, documents[i].Text, json, vectors[i]));
        }
        await _backend.UpsertAsync(_options.TableName, rows, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        IReadOnlyDictionary<string, object?>? filters = null,
        CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
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
        var hits = await _backend.QueryAsync(_options.TableName, queryVector, topK, eq, ct).ConfigureAwait(false);
        var output = new List<(RetrievedDocument, double)>(hits.Count);
        foreach (var (id, text, metaJson, score) in hits)
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(metaJson) ?? new();
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
        await _backend.EnsureSchemaAsync(_options.TableName, _options.VectorDimension, ct).ConfigureAwait(false);
        _ensured = true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _backend.DisposeAsync();
}
