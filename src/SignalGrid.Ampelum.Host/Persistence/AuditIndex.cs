using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>SQLite-backed implementation of <see cref="IAuditIndex"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AuditIndex : IAuditIndex
{
    readonly IHostSqliteConnectionFactory _factory;

    /// <summary>Initializes the audit index with the given connection factory.</summary>
    public AuditIndex(IHostSqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public ValueTask UpsertAsync(AuditIndexRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO audit_index(trace_id, sequence, timestamp, module, event_type)
            VALUES(@traceId, @sequence, @timestamp, @module, @eventType);
            """;
        cmd.Parameters.AddWithValue("@traceId", row.TraceId);
        cmd.Parameters.AddWithValue("@sequence", row.Sequence);
        cmd.Parameters.AddWithValue("@timestamp", row.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@module", row.Module);
        cmd.Parameters.AddWithValue("@eventType", row.EventType);
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<AuditIndexRow?> TryGetByTraceIdAsync(string traceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(traceId);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT trace_id, sequence, timestamp, module, event_type FROM audit_index WHERE trace_id=@id;";
        cmd.Parameters.AddWithValue("@id", traceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ValueTask.FromResult<AuditIndexRow?>(null);
        return ValueTask.FromResult<AuditIndexRow?>(new AuditIndexRow(
            r.GetString(0), r.GetInt64(1),
            DateTimeOffset.Parse(r.GetString(2)),
            r.GetString(3), r.GetString(4)));
    }

    /// <inheritdoc/>
    public ValueTask<long> CountAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_index;";
        return ValueTask.FromResult((long)(cmd.ExecuteScalar() ?? 0L));
    }

    /// <inheritdoc/>
    public ValueTask DeleteAllAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM audit_index;";
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }
}
