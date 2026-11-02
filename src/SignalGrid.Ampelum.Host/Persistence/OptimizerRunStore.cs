using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>SQLite-backed implementation of <see cref="IOptimizerRunStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class OptimizerRunStore : IOptimizerRunStore
{
    readonly IHostSqliteConnectionFactory _factory;

    /// <summary>Initializes the store with the given connection factory.</summary>
    public OptimizerRunStore(IHostSqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public ValueTask InsertAsync(OptimizerRunRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO optimizer_runs(run_id, module_name, optimizer_type, state, started_at, completed_at, trials_completed, trials_total, result_version_id, error_message)
            VALUES(@rid, @mod, @ot, 'queued', @sat, @cat, @tc, @tt, @rvid, @em);
            """;
        cmd.Parameters.AddWithValue("@rid", row.RunId);
        cmd.Parameters.AddWithValue("@mod", row.ModuleName);
        cmd.Parameters.AddWithValue("@ot", row.OptimizerType);
        cmd.Parameters.AddWithValue("@sat", row.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@cat", (object?)row.CompletedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tc", row.TrialsCompleted);
        cmd.Parameters.AddWithValue("@tt", (object?)row.TrialsTotal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rvid", (object?)row.ResultVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@em", (object?)row.ErrorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask UpdateProgressAsync(string runId, int trialsCompleted, int? trialsTotal, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE optimizer_runs
            SET state = CASE WHEN state='queued' THEN 'running' ELSE state END,
                trials_completed = @tc,
                trials_total = @tt
            WHERE run_id = @rid AND state IN ('queued', 'running');
            """;
        cmd.Parameters.AddWithValue("@rid", runId);
        cmd.Parameters.AddWithValue("@tc", trialsCompleted);
        cmd.Parameters.AddWithValue("@tt", (object?)trialsTotal ?? DBNull.Value);
        var updated = cmd.ExecuteNonQuery();
        if (updated == 0)
            throw new InvalidOperationException($"Run '{runId}' not found or not in queued/running state.");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask CompleteAsync(string runId, string finalState, string? resultVersionId, string? errorMessage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(finalState);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state FROM optimizer_runs WHERE run_id=@rid;";
        cmd.Parameters.AddWithValue("@rid", runId);
        var currentState = cmd.ExecuteScalar() as string;
        if (currentState != "running")
            throw new InvalidOperationException(
                $"Cannot complete run '{runId}': current state '{currentState ?? "not found"}' is not 'running'.");

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = """
            UPDATE optimizer_runs
            SET state = @fs, completed_at = @cat, result_version_id = @rvid, error_message = @em
            WHERE run_id = @rid;
            """;
        updateCmd.Parameters.AddWithValue("@rid", runId);
        updateCmd.Parameters.AddWithValue("@fs", finalState);
        updateCmd.Parameters.AddWithValue("@cat", DateTimeOffset.UtcNow.ToString("O"));
        updateCmd.Parameters.AddWithValue("@rvid", (object?)resultVersionId ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@em", (object?)errorMessage ?? DBNull.Value);
        updateCmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<OptimizerRunRow?> TryGetAsync(string runId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, module_name, optimizer_type, state, started_at, completed_at, trials_completed, trials_total, result_version_id, error_message
            FROM optimizer_runs WHERE run_id=@rid;
            """;
        cmd.Parameters.AddWithValue("@rid", runId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ValueTask.FromResult<OptimizerRunRow?>(null);
        return ValueTask.FromResult<OptimizerRunRow?>(ReadRow(r));
    }

    /// <inheritdoc/>
    public ValueTask DeleteAllAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM optimizer_runs;";
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    private static OptimizerRunRow ReadRow(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)),
            r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9));
}
