using Npgsql;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>
/// Where a run keeps its checkpoints and audit history: a SQLite file or PostgreSQL. Every call to a factory
/// gives a new persistence object, so a resumed run never shares memory with the run it resumes.
/// </summary>
public sealed class ExpenseStore
{
    /// <summary>Name of the setting that holds the PostgreSQL connection string.</summary>
    public const string PostgresSetting = "WORKFLOW_POSTGRES";

    readonly Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRuntimeAuditEvent>>> _queryAudit;
    readonly Func<string, CancellationToken, Task> _delete;

    ExpenseStore(
        IWorkflowCheckpointTransactionStore persistence,
        Func<string, CancellationToken, Task<IReadOnlyList<WorkflowRuntimeAuditEvent>>> queryAudit,
        Func<string, CancellationToken, Task> delete)
    {
        Persistence = persistence;
        _queryAudit = queryAudit;
        _delete = delete;
    }

    /// <summary>The persistence the workflow runtime uses for both state and audit.</summary>
    public IWorkflowCheckpointTransactionStore Persistence { get; }

    /// <summary>Opens a SQLite file store.</summary>
    public static ExpenseStore Sqlite(string databasePath)
    {
        var store = new SqliteWorkflowPersistence(databasePath);
        return new ExpenseStore(store, store.QueryAuditAsync, (_, _) => Task.CompletedTask);
    }

    /// <summary>Opens a PostgreSQL store. A blank connection string is an error that names the setting.</summary>
    public static ExpenseStore Postgres(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Missing setting {PostgresSetting}. Copy .sample.env to .env and set it.");
        var store = new PostgresWorkflowPersistence(connectionString);
        return new ExpenseStore(store, store.QueryAuditAsync, (id, ct) => DeleteRowsAsync(connectionString, id, ct));
    }

    /// <summary>Picks PostgreSQL when <paramref name="postgres"/> is set, otherwise the SQLite file. No fallback between them.</summary>
    public static ExpenseStore Create(bool postgres, Func<string, string> readSetting, string sqlitePath)
    {
        if (!postgres)
            return Sqlite(sqlitePath);
        ArgumentNullException.ThrowIfNull(readSetting);
        return Postgres(readSetting(PostgresSetting));
    }

    /// <summary>Loads the stored snapshot of an execution, or null.</summary>
    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default) =>
        Persistence.LoadAsync(executionId, ct);

    /// <summary>Reads the audit history of an execution in order.</summary>
    public Task<IReadOnlyList<WorkflowRuntimeAuditEvent>> QueryAuditAsync(string executionId, CancellationToken ct = default) =>
        _queryAudit(executionId, ct);

    /// <summary>Removes the rows one execution wrote. Does nothing for a SQLite file, which is deleted with its folder.</summary>
    public Task DeleteExecutionAsync(string executionId, CancellationToken ct = default) =>
        _delete(executionId, ct);

    static async Task DeleteRowsAsync(string connectionString, string executionId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            DELETE FROM ampelum_workflow_runtime_audit WHERE execution_id = @executionId;
            DELETE FROM ampelum_workflow_checkpoints WHERE execution_id = @executionId;
            """, connection);
        command.Parameters.AddWithValue("executionId", executionId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
