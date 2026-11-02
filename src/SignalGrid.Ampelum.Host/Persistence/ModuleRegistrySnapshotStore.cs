using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>SQLite-backed implementation of <see cref="IModuleRegistrySnapshotStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModuleRegistrySnapshotStore : IModuleRegistrySnapshotStore
{
    readonly IHostSqliteConnectionFactory _factory;

    /// <summary>Initializes the store with the given connection factory.</summary>
    public ModuleRegistrySnapshotStore(IHostSqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public ValueTask UpsertAsync(ModuleSnapshotRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO module_registry(module_name, signature_type, supports_stream, registered_at)
            VALUES(@mn, @st, @ss, @rat);
            """;
        cmd.Parameters.AddWithValue("@mn", row.ModuleName);
        cmd.Parameters.AddWithValue("@st", row.SignatureType);
        cmd.Parameters.AddWithValue("@ss", row.SupportsStream ? 1 : 0);
        cmd.Parameters.AddWithValue("@rat", row.RegisteredAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ModuleSnapshotRow>> ListAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT module_name, signature_type, supports_stream, registered_at FROM module_registry ORDER BY module_name;";
        using var r = cmd.ExecuteReader();
        var list = new List<ModuleSnapshotRow>();
        while (r.Read())
            list.Add(new ModuleSnapshotRow(r.GetString(0), r.GetString(1), r.GetInt32(2) != 0, DateTimeOffset.Parse(r.GetString(3))));
        return ValueTask.FromResult<IReadOnlyList<ModuleSnapshotRow>>(list);
    }

    /// <inheritdoc/>
    public ValueTask DeleteAllAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM module_registry;";
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }
}
