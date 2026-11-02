using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Lifecycle state for a policy confirmation. A pending confirmation is
/// never an authorization to execute a tool.</summary>
public enum ToolConfirmationStatus { Pending, Approved, Rejected, Expired }

/// <summary>Approval artifact bound to one caller, tool, trace and workflow.</summary>
public sealed record ToolConfirmation(
    string ConfirmationId,
    string ToolName,
    string ActorId,
    string TraceId,
    string WorkflowId,
    ToolConfirmationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt = null,
    string? DecidedBy = null,
    string? DecisionReason = null);

public interface IToolConfirmationStore
{
    Task<ToolConfirmation> CreateAsync(ToolConfirmation confirmation, CancellationToken ct = default);
    Task<ToolConfirmation?> GetAsync(string confirmationId, CancellationToken ct = default);
    Task<ToolConfirmation?> DecideAsync(string confirmationId, bool approved, string decidedBy, string? reason = null, CancellationToken ct = default);
}

/// <summary>Thread-safe process-local confirmation store. It is useful for tests
/// and single-process development only; deployments need a durable implementation.</summary>
public sealed class InMemoryToolConfirmationStore : IToolConfirmationStore
{
    private readonly ConcurrentDictionary<string, ToolConfirmation> _items = new(StringComparer.Ordinal);

    public Task<ToolConfirmation> CreateAsync(ToolConfirmation confirmation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!_items.TryAdd(confirmation.ConfirmationId, confirmation))
            throw new InvalidOperationException($"Confirmation '{confirmation.ConfirmationId}' already exists.");
        return Task.FromResult(confirmation);
    }

    public Task<ToolConfirmation?> GetAsync(string confirmationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);
        if (!_items.TryGetValue(confirmationId, out var confirmation)) return Task.FromResult<ToolConfirmation?>(null);
        if (confirmation.Status == ToolConfirmationStatus.Pending && confirmation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            confirmation = confirmation with { Status = ToolConfirmationStatus.Expired };
            _items[confirmationId] = confirmation;
        }
        return Task.FromResult<ToolConfirmation?>(confirmation);
    }

    public async Task<ToolConfirmation?> DecideAsync(string confirmationId, bool approved, string decidedBy, string? reason = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        var current = await GetAsync(confirmationId, ct).ConfigureAwait(false);
        if (current is null || current.Status != ToolConfirmationStatus.Pending) return current;
        var next = current with
        {
            Status = approved ? ToolConfirmationStatus.Approved : ToolConfirmationStatus.Rejected,
            DecidedAt = DateTimeOffset.UtcNow,
            DecidedBy = decidedBy,
            DecisionReason = reason
        };
        return _items.TryUpdate(confirmationId, next, current) ? next : await GetAsync(confirmationId, ct).ConfigureAwait(false);
    }
}

/// <summary>Append-only process-local audit sink. Sequence numbers provide a
/// total order for one dispatcher process. It is not crash durable.</summary>
public sealed class InMemoryToolAuditLog : IToolAuditLog
{
    private readonly ConcurrentQueue<ToolAuditEntry> _events = new();
    private long _sequence;

    public IReadOnlyList<ToolAuditEntry> Entries => _events.ToArray();

    public Task RecordAsync(ToolAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sequence = entry.Sequence == 0 ? Interlocked.Increment(ref _sequence) : entry.Sequence;
        _events.Enqueue(entry with { Sequence = sequence });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ToolAuditEntry>> QueryAsync(string? toolName = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = _events.Where(x => toolName is null || StringComparer.Ordinal.Equals(x.ToolName, toolName))
            .OrderBy(x => x.Sequence).TakeLast(limit).ToArray();
        return Task.FromResult<IReadOnlyList<ToolAuditEntry>>(result);
    }
}

/// <summary>SQLite-backed append-only audit log. This is the durable default
/// implementation available without a service dependency; callers own backup,
/// retention and encryption of the database file.</summary>
public sealed class SqliteToolAuditLog : IToolAuditLog
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteToolAuditLog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task RecordAsync(ToolAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tool_governance_audit (audit_id, tool_name, actor_id, actor_type, args_json, decision, decision_reason, result_json, timestamp_utc, trace_id, workflow_id, event_type, confirmation_id, observation_token_id)
                VALUES ($auditId, $toolName, $actorId, $actorType, $args, $decision, $reason, $result, $timestamp, $traceId, $workflowId, $eventType, $confirmationId, $tokenId);
                """;
            command.Parameters.AddWithValue("$auditId", entry.AuditId);
            command.Parameters.AddWithValue("$toolName", entry.ToolName);
            command.Parameters.AddWithValue("$actorId", entry.ActorId);
            command.Parameters.AddWithValue("$actorType", entry.ActorType);
            command.Parameters.AddWithValue("$args", JsonSerializer.Serialize(entry.Args.ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "[null]", StringComparer.Ordinal)));
            command.Parameters.AddWithValue("$decision", entry.PolicyDecision.Decision.ToString());
            command.Parameters.AddWithValue("$reason", (object?)entry.PolicyDecision.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$result", entry.Result is null ? DBNull.Value : JsonSerializer.Serialize(new PersistedToolResult(entry.Result.Success, entry.Result.Output, entry.Result.Error, entry.Result.ExecutedAt, entry.Result.Duration, entry.Result.ObservationTokenId)));
            command.Parameters.AddWithValue("$timestamp", entry.Timestamp.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$traceId", (object?)entry.TraceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$workflowId", (object?)entry.WorkflowId ?? DBNull.Value);
            command.Parameters.AddWithValue("$eventType", entry.EventType);
            command.Parameters.AddWithValue("$confirmationId", (object?)entry.ConfirmationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokenId", (object?)entry.ObservationTokenId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ToolAuditEntry>> QueryAsync(string? toolName = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, audit_id, tool_name, actor_id, actor_type, args_json, decision, decision_reason, result_json, timestamp_utc, trace_id, workflow_id, event_type, confirmation_id, observation_token_id
                FROM tool_governance_audit WHERE $toolName IS NULL OR tool_name = $toolName ORDER BY sequence DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$toolName", (object?)toolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<ToolAuditEntry>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var decision = new PolicyDecision(Enum.Parse<Decision>(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7));
                var persistedArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5)) ?? [];
                IReadOnlyDictionary<string, object?> args = persistedArgs.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal);
                var persistedResult = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<PersistedToolResult>(reader.GetString(8));
                var result = persistedResult is null ? null : new ToolResult(reader.GetString(2), persistedResult.Success, persistedResult.Output, persistedResult.Error, persistedResult.ExecutedAt, persistedResult.Duration, persistedResult.ObservationTokenId);
                list.Add(new ToolAuditEntry(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), args, decision, result, DateTimeOffset.Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14), reader.GetInt64(0)));
            }
            list.Reverse();
            return list;
        }
        finally { _gate.Release(); }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_governance_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, audit_id TEXT NOT NULL, tool_name TEXT NOT NULL, actor_id TEXT NOT NULL, actor_type TEXT NOT NULL,
                args_json TEXT NOT NULL, decision TEXT NOT NULL, decision_reason TEXT NULL, result_json TEXT NULL, timestamp_utc TEXT NOT NULL,
                trace_id TEXT NULL, workflow_id TEXT NULL, event_type TEXT NOT NULL, confirmation_id TEXT NULL, observation_token_id TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_tool_governance_audit_trace ON tool_governance_audit(trace_id, workflow_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record PersistedToolResult(bool Success, string? Output, string? Error, DateTimeOffset ExecutedAt, TimeSpan Duration, string? ObservationTokenId = null);
}
