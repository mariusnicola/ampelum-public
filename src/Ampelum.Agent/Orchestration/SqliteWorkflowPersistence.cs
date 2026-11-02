using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>
/// SQLite-backed workflow checkpoint and audit persistence for a single host.
/// Use one instance as both dependencies of <see cref="DurableWorkflowRuntime"/>
/// to atomically commit every state transition with its audit event.
/// </summary>
/// <remarks>
/// SQLite WAL permits independent processes on one local host to coordinate through the
/// same database file. Callers own database-file encryption, backup, retention, and
/// storage placement; network filesystems with unreliable SQLite locking are unsupported.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SqliteWorkflowPersistence : IWorkflowCheckpointTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    /// <summary>Creates and initializes a workflow persistence database at <paramref name="databasePath"/>.</summary>
    public SqliteWorkflowPersistence(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM workflow_checkpoints WHERE execution_id = $executionId;";
        command.Parameters.AddWithValue("$executionId", executionId);
        var value = command.ExecuteScalar() as string;
        return Task.FromResult(value is null ? null : DeserializeSnapshot(value));
    }

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var created = TryCreate(connection, transaction, snapshot);
        transaction.Commit();
        return Task.FromResult(created);
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default)
    {
        ValidateUpdate(executionId, expected, next);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var updated = TryUpdate(connection, transaction, executionId, expected, next);
        transaction.Commit();
        return Task.FromResult(updated);
    }

    /// <inheritdoc />
    public Task<bool> TryCreateAndAppendAsync(DurableWorkflowSnapshot snapshot, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAudit(auditEvent, snapshot.Request.ExecutionId);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var created = TryCreate(connection, transaction, snapshot);
        if (created) Append(connection, transaction, auditEvent);
        transaction.Commit();
        return Task.FromResult(created);
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAndAppendAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ValidateUpdate(executionId, expected, next);
        ValidateAudit(auditEvent, executionId);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var updated = TryUpdate(connection, transaction, executionId, expected, next);
        if (updated) Append(connection, transaction, auditEvent);
        transaction.Commit();
        return Task.FromResult(updated);
    }

    /// <inheritdoc />
    public Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Append(connection, transaction, auditEvent);
        transaction.Commit();
        return Task.CompletedTask;
    }

    /// <summary>Reads the append-only audit history in database order for one workflow execution.</summary>
    public Task<IReadOnlyList<WorkflowRuntimeAuditEvent>> QueryAuditAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, execution_id, event_type, step_id, status, occurred_at_utc, detail
            FROM workflow_runtime_audit WHERE execution_id = $executionId ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        using var reader = command.ExecuteReader();
        var events = new List<WorkflowRuntimeAuditEvent>();
        while (reader.Read())
        {
            events.Add(new WorkflowRuntimeAuditEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<WorkflowExecutionStatus>(reader.GetString(4), ignoreCase: false),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return Task.FromResult<IReadOnlyList<WorkflowRuntimeAuditEvent>>(events);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS workflow_checkpoints (
                    execution_id TEXT PRIMARY KEY,
                    snapshot_json TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 0,
                    updated_at_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS workflow_runtime_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    execution_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    step_id TEXT NULL,
                    status TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    detail TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_workflow_runtime_audit_execution_sequence
                    ON workflow_runtime_audit(execution_id, sequence);
                """;
            command.ExecuteNonQuery();
        }

        // `CREATE TABLE IF NOT EXISTS` above is a no-op against a database file created by a version of this
        // provider that predates the `version` column (rc3 and earlier). Without this migration, every write
        // against such a pre-existing database file throws "no such column: version" the moment this ships.
        if (!HasVersionColumn(connection))
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE workflow_checkpoints ADD COLUMN version INTEGER NOT NULL DEFAULT 0;";
            migrate.ExecuteNonQuery();
        }
    }

    private static bool HasVersionColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('workflow_checkpoints') WHERE name = 'version';";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool TryCreate(SqliteConnection connection, SqliteTransaction transaction, DurableWorkflowSnapshot snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workflow_checkpoints (execution_id, snapshot_json, version, updated_at_utc)
            VALUES ($executionId, $snapshotJson, $version, $updatedAtUtc)
            ON CONFLICT(execution_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$executionId", snapshot.Request.ExecutionId);
        command.Parameters.AddWithValue("$snapshotJson", SerializeSnapshot(snapshot));
        command.Parameters.AddWithValue("$version", snapshot.Version);
        command.Parameters.AddWithValue("$updatedAtUtc", snapshot.UpdatedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1;
    }

    private static bool TryUpdate(SqliteConnection connection, SqliteTransaction transaction, string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workflow_checkpoints
            SET snapshot_json = $nextJson, version = $nextVersion, updated_at_utc = $updatedAtUtc
            WHERE execution_id = $executionId AND version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$expectedVersion", expected.Version);
        command.Parameters.AddWithValue("$nextJson", SerializeSnapshot(next));
        command.Parameters.AddWithValue("$nextVersion", next.Version);
        command.Parameters.AddWithValue("$updatedAtUtc", next.UpdatedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1;
    }

    private static void Append(SqliteConnection connection, SqliteTransaction transaction, WorkflowRuntimeAuditEvent auditEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workflow_runtime_audit (execution_id, event_type, step_id, status, occurred_at_utc, detail)
            VALUES ($executionId, $eventType, $stepId, $status, $occurredAtUtc, $detail);
            """;
        command.Parameters.AddWithValue("$executionId", auditEvent.ExecutionId);
        command.Parameters.AddWithValue("$eventType", auditEvent.EventType);
        command.Parameters.AddWithValue("$stepId", (object?)auditEvent.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", auditEvent.Status.ToString());
        command.Parameters.AddWithValue("$occurredAtUtc", auditEvent.OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$detail", (object?)auditEvent.Detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void ValidateUpdate(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(next);
        if (!StringComparer.Ordinal.Equals(executionId, expected.Request.ExecutionId) || !StringComparer.Ordinal.Equals(executionId, next.Request.ExecutionId))
            throw new ArgumentException("The execution identifier must match both workflow snapshots.", nameof(executionId));
    }

    private static void ValidateAudit(WorkflowRuntimeAuditEvent auditEvent, string executionId)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (!StringComparer.Ordinal.Equals(executionId, auditEvent.ExecutionId))
            throw new ArgumentException("The audit event execution identifier must match the workflow checkpoint.", nameof(auditEvent));
    }

    private static string SerializeSnapshot(DurableWorkflowSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    private static DurableWorkflowSnapshot DeserializeSnapshot(string json) =>
        JsonSerializer.Deserialize<DurableWorkflowSnapshot>(json, JsonOptions)
        ?? throw new InvalidOperationException("Workflow checkpoint JSON was invalid.");
}
