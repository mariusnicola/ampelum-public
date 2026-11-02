using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Pgvector;

namespace SignalGrid.Ampelum.VectorStores.PgVector;

/// <summary>Default <see cref="IPgVectorBackend"/> wrapping <see cref="NpgsqlDataSource"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class NpgsqlPgVectorBackend : IPgVectorBackend
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlPgVectorBackend(PgVectorOptions options)
    {
        var b = new NpgsqlDataSourceBuilder(options.ConnectionString);
        b.UseVector();
        _dataSource = b.Build();
    }

    public async Task EnsureSchemaAsync(string table, int dim, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(PgVectorSchema.CreateTable(table, dim));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(string table, IReadOnlyList<(string Id, string Text, string MetadataJson, ReadOnlyMemory<float> Embedding)> rows, CancellationToken ct)
    {
        var sql = $"INSERT INTO {table} (id, text, metadata, embedding) VALUES (@id, @text, @meta::jsonb, @emb) "
                  + "ON CONFLICT (id) DO UPDATE SET text = EXCLUDED.text, metadata = EXCLUDED.metadata, embedding = EXCLUDED.embedding";
        foreach (var (id, text, metaJson, emb) in rows)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("text", text);
            cmd.Parameters.AddWithValue("meta", metaJson);
            cmd.Parameters.AddWithValue("emb", new Vector(emb.ToArray()));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<(string Id, string Text, string MetadataJson, double Score)>> QueryAsync(string table, ReadOnlyMemory<float> queryVector, int topK, IReadOnlyDictionary<string, string>? filterEquals, CancellationToken ct)
    {
        var paramList = new List<NpgsqlParameter>();
        var where = string.Empty;
        if (filterEquals is not null && filterEquals.Count > 0)
        {
            var clauses = new List<string>();
            int i = 0;
            foreach (var f in filterEquals)
            {
                var pname = $"f{i++}";
                paramList.Add(new NpgsqlParameter(pname, f.Value));
                clauses.Add($"metadata ->> '{f.Key}' = @{pname}");
            }
            where = "WHERE " + string.Join(" AND ", clauses);
        }
        var sql = $"SELECT id, text, metadata, 1 - (embedding <=> @q) AS score FROM {table} {where} ORDER BY embedding <=> @q LIMIT @k";
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("q", new Vector(queryVector.ToArray()));
        cmd.Parameters.AddWithValue("k", topK);
        foreach (var p in paramList)
        {
            cmd.Parameters.Add(p);
        }
        var output = new List<(string, string, string, double)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            output.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        }
        return output;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
