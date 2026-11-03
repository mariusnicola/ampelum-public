using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SignalGrid.Ampelum.Agent.Contracts;

namespace SignalGrid.Ampelum.Agent.Orchestration;

/// <summary>
/// PostgreSQL-backed workflow checkpoint and audit persistence for multi-process deployments.
/// Use one instance as both dependencies of <see cref="DurableWorkflowRuntime"/> so every
/// state transition and audit record commits in the same database transaction.
/// </summary>
/// <remarks>
/// The configured PostgreSQL role needs DDL rights only while initializing this provider and
/// DML rights thereafter. Production operators own TLS, role separation, backup, retention,
/// high availability, and database migration/change control.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PostgresWorkflowPersistence : IWorkflowCheckpointTransactionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    /// <summary>Creates and initializes workflow persistence tables using <paramref name="connectionString"/>.</summary>
    public PostgresWorkflowPersistence(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        EnsureSchema();
    }

    /// <inheritdoc />
    public async Task<DurableWorkflowSnapshot?> LoadAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT snapshot_json::text FROM ampelum_workflow_checkpoints WHERE execution_id = @executionId;", connection);
        command.Parameters.AddWithValue("executionId", executionId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return value is null ? null : DeserializeSnapshot(value);
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(DurableWorkflowSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var created = await TryCreateAsync(connection, transaction, snapshot, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct = default)
    {
        ValidateUpdate(executionId, expected, next);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var updated = await TryUpdateAsync(connection, transaction, executionId, expected, next, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAndAppendAsync(DurableWorkflowSnapshot snapshot, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAudit(auditEvent, snapshot.Request.ExecutionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var created = await TryCreateAsync(connection, transaction, snapshot, ct).ConfigureAwait(false);
        if (created) await AppendAsync(connection, transaction, auditEvent, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAndAppendAsync(string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ValidateUpdate(executionId, expected, next);
        ValidateAudit(auditEvent, executionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var updated = await TryUpdateAsync(connection, transaction, executionId, expected, next, ct).ConfigureAwait(false);
        if (updated) await AppendAsync(connection, transaction, auditEvent, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task AppendAsync(WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await AppendAsync(connection, transaction, auditEvent, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads append-only audit history in database sequence order for one workflow execution.</summary>
    public async Task<IReadOnlyList<WorkflowRuntimeAuditEvent>> QueryAuditAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT sequence, execution_id, event_type, step_id, status, occurred_at_utc, detail
            FROM ampelum_workflow_runtime_audit WHERE execution_id = @executionId ORDER BY sequence;
            """, connection);
        command.Parameters.AddWithValue("executionId", executionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var events = new List<WorkflowRuntimeAuditEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new WorkflowRuntimeAuditEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                Enum.Parse<WorkflowExecutionStatus>(reader.GetString(4), ignoreCase: false),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return events;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    // Arbitrary constant that names this provider's schema lock.
    private const long SchemaLockKey = 0x416D70_57464C;

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        // The DDL below locks both tables, the ALTER exclusively, even when nothing changes. Run against a
        // database in use, it deadlocks with a writer that holds a checkpoint row and waits on the audit table,
        // so it runs only when part of the schema is missing.
        using (var check = new NpgsqlCommand("""
            SELECT to_regclass('ampelum_workflow_runtime_audit') IS NOT NULL
               AND to_regclass('ix_ampelum_workflow_runtime_audit_execution_sequence') IS NOT NULL
               AND EXISTS (SELECT 1 FROM pg_attribute
                           WHERE attrelid = to_regclass('ampelum_workflow_checkpoints')
                             AND attname = 'version' AND NOT attisdropped);
            """, connection))
        {
            if (check.ExecuteScalar() is true) return;
        }

        using var transaction = connection.BeginTransaction();
        using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key);", connection, transaction))
        {
            gate.Parameters.AddWithValue("key", SchemaLockKey);
            gate.ExecuteNonQuery();
        }
        using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS ampelum_workflow_checkpoints (
                execution_id TEXT PRIMARY KEY,
                snapshot_json JSONB NOT NULL,
                version BIGINT NOT NULL DEFAULT 0,
                updated_at_utc TIMESTAMPTZ NOT NULL);
            CREATE TABLE IF NOT EXISTS ampelum_workflow_runtime_audit (
                sequence BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                execution_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                step_id TEXT NULL,
                status TEXT NOT NULL,
                occurred_at_utc TIMESTAMPTZ NOT NULL,
                detail TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_ampelum_workflow_runtime_audit_execution_sequence
                ON ampelum_workflow_runtime_audit(execution_id, sequence);
            -- `CREATE TABLE IF NOT EXISTS` above is a no-op against a database created by a version of this
            -- provider that predates the `version` column (rc3 and earlier). Without this migration, every
            -- write against such a pre-existing database throws "column "version" does not exist" the moment
            -- this ships.
            ALTER TABLE ampelum_workflow_checkpoints ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 0;
            """, connection, transaction);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static async Task<bool> TryCreateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DurableWorkflowSnapshot snapshot, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ampelum_workflow_checkpoints (execution_id, snapshot_json, version, updated_at_utc)
            VALUES (@executionId, @snapshotJson, @version, @updatedAtUtc)
            ON CONFLICT (execution_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("executionId", snapshot.Request.ExecutionId);
        command.Parameters.AddWithValue("snapshotJson", NpgsqlDbType.Jsonb, SerializeSnapshot(snapshot));
        command.Parameters.AddWithValue("version", snapshot.Version);
        command.Parameters.AddWithValue("updatedAtUtc", snapshot.UpdatedAt);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> TryUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string executionId, DurableWorkflowSnapshot expected, DurableWorkflowSnapshot next, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE ampelum_workflow_checkpoints
            SET snapshot_json = @nextJson, version = @nextVersion, updated_at_utc = @updatedAtUtc
            WHERE execution_id = @executionId AND version = @expectedVersion;
            """, connection, transaction);
        command.Parameters.AddWithValue("executionId", executionId);
        command.Parameters.AddWithValue("expectedVersion", expected.Version);
        command.Parameters.AddWithValue("nextJson", NpgsqlDbType.Jsonb, SerializeSnapshot(next));
        command.Parameters.AddWithValue("nextVersion", next.Version);
        command.Parameters.AddWithValue("updatedAtUtc", next.UpdatedAt);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, WorkflowRuntimeAuditEvent auditEvent, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ampelum_workflow_runtime_audit (execution_id, event_type, step_id, status, occurred_at_utc, detail)
            VALUES (@executionId, @eventType, @stepId, @status, @occurredAtUtc, @detail);
            """, connection, transaction);
        command.Parameters.AddWithValue("executionId", auditEvent.ExecutionId);
        command.Parameters.AddWithValue("eventType", auditEvent.EventType);
        command.Parameters.AddWithValue("stepId", (object?)auditEvent.StepId ?? DBNull.Value);
        command.Parameters.AddWithValue("status", auditEvent.Status.ToString());
        command.Parameters.AddWithValue("occurredAtUtc", auditEvent.OccurredAt);
        command.Parameters.AddWithValue("detail", (object?)auditEvent.Detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
