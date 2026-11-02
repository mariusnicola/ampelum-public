using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Agent;

/// <summary>SQLite-backed durable observation-token store. It is safe for multiple
/// processes using the same database file because consumption is one conditional
/// update inside a SQLite transaction. Callers own file encryption, backups and
/// retention.</summary>
public sealed class SqliteObservationTokenStore : IDurableObservationTokenStore
{
    private readonly string _connectionString;

    public SqliteObservationTokenStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public void Store(ObservationToken token) => throw new InvalidOperationException("Durable observation tokens require scope, expiry, and audit context.");

    /// <inheritdoc />
    public void Store(ObservationToken token, ObservationTokenScope scope, DateTimeOffset expiresAt, ObservationTokenAuditContext context)
    {
        ArgumentNullException.ThrowIfNull(token);
        scope.Validate();
        context.Validate();
        if (expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observation_tokens (token_id, resource_path, issued_by_tool, workspace_id, session_id, issuer_id, issued_at_call_index, expires_at_call_index, expires_at_utc, status)
            VALUES ($tokenId, $resourcePath, $issuedByTool, $workspaceId, $sessionId, $issuerId, $issuedAt, $expiresAt, $expiresUtc, 'active');
            """;
        command.Parameters.AddWithValue("$tokenId", token.TokenId);
        command.Parameters.AddWithValue("$resourcePath", token.ResourcePath);
        command.Parameters.AddWithValue("$issuedByTool", token.IssuedByTool);
        command.Parameters.AddWithValue("$workspaceId", scope.WorkspaceId);
        command.Parameters.AddWithValue("$sessionId", scope.SessionId);
        command.Parameters.AddWithValue("$issuerId", scope.IssuerId);
        command.Parameters.AddWithValue("$issuedAt", token.IssuedAtCallIndex);
        command.Parameters.AddWithValue("$expiresAt", token.ExpiresAtCallIndex);
        command.Parameters.AddWithValue("$expiresUtc", expiresAt.UtcDateTime.ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Observation token '{token.TokenId}' already exists.");
        AppendAudit(connection, transaction, token.TokenId, "issued", scope, context, null);
        transaction.Commit();
    }

    /// <inheritdoc />
    public ObservationToken? Resolve(string tokenId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT token_id, resource_path, issued_by_tool, session_id, issued_at_call_index, expires_at_call_index
            FROM observation_tokens WHERE token_id = $tokenId AND status = 'active' AND expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$tokenId", tokenId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ObservationToken(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)) : null;
    }

    /// <inheritdoc />
    public ObservationTokenConsumptionResult Consume(string tokenId, ObservationTokenScope scope, string? resourcePath, ObservationTokenAuditContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        scope.Validate();
        context.Validate();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE observation_tokens SET status = 'consumed', consumed_at_utc = $now
            WHERE token_id = $tokenId AND status = 'active' AND expires_at_utc > $now
              AND workspace_id = $workspaceId AND session_id = $sessionId AND issuer_id = $issuerId
              AND ($resourcePath IS NULL OR resource_path = $resourcePath);
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$tokenId", tokenId);
        command.Parameters.AddWithValue("$resourcePath", (object?)resourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        if (command.ExecuteNonQuery() == 1)
        {
            AppendAudit(connection, transaction, tokenId, "consumed", scope, context, null);
            transaction.Commit();
            return new ObservationTokenConsumptionResult(true);
        }

        var reason = GetRejectionReason(connection, transaction, tokenId, scope, resourcePath);
        AppendAudit(connection, transaction, tokenId, "rejected", scope, context, reason);
        transaction.Commit();
        return new ObservationTokenConsumptionResult(false, reason);
    }

    /// <inheritdoc />
    public bool Revoke(string tokenId, ObservationTokenScope scope, string reason, ObservationTokenAuditContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        scope.Validate();
        context.Validate();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE observation_tokens SET status = 'revoked', revoked_at_utc = $now, revocation_reason = $reason
            WHERE token_id = $tokenId AND status = 'active' AND workspace_id = $workspaceId AND session_id = $sessionId AND issuer_id = $issuerId;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$tokenId", tokenId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        var revoked = command.ExecuteNonQuery() == 1;
        if (revoked) AppendAudit(connection, transaction, tokenId, "revoked", scope, context, reason);
        else AppendAudit(connection, transaction, tokenId, "rejected", scope, context, "Token cannot be revoked because it is missing, inactive, or bound to another authority.");
        transaction.Commit();
        return revoked;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObservationTokenAuditEvent>> QueryAuditAsync(string? tokenId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, token_id, event_type, occurred_at_utc, workspace_id, session_id, issuer_id, trace_id, workflow_id, actor_id, reason
            FROM observation_token_audit WHERE $tokenId IS NULL OR token_id = $tokenId ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$tokenId", (object?)tokenId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var events = new List<ObservationTokenAuditEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new ObservationTokenAuditEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), new ObservationTokenScope(reader.GetString(4), reader.GetString(5), reader.GetString(6)), new ObservationTokenAuditContext(reader.GetString(7), reader.GetString(8), reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return events;
    }

    /// <inheritdoc />
    public void PurgeSession() { }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS observation_tokens (
                token_id TEXT PRIMARY KEY, resource_path TEXT NOT NULL, issued_by_tool TEXT NOT NULL,
                workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, issuer_id TEXT NOT NULL,
                issued_at_call_index INTEGER NOT NULL, expires_at_call_index INTEGER NOT NULL, expires_at_utc TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('active', 'consumed', 'revoked')),
                consumed_at_utc TEXT NULL, revoked_at_utc TEXT NULL, revocation_reason TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_observation_tokens_active ON observation_tokens(token_id, workspace_id, session_id, issuer_id) WHERE status = 'active';
            CREATE TABLE IF NOT EXISTS observation_token_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, token_id TEXT NOT NULL, event_type TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL, workspace_id TEXT NOT NULL, session_id TEXT NOT NULL, issuer_id TEXT NOT NULL,
                trace_id TEXT NOT NULL, workflow_id TEXT NOT NULL, actor_id TEXT NOT NULL, reason TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_observation_token_audit_token ON observation_token_audit(token_id, sequence);
            """;
        command.ExecuteNonQuery();
    }

    private static void AddScope(SqliteCommand command, ObservationTokenScope scope)
    {
        command.Parameters.AddWithValue("$workspaceId", scope.WorkspaceId);
        command.Parameters.AddWithValue("$sessionId", scope.SessionId);
        command.Parameters.AddWithValue("$issuerId", scope.IssuerId);
    }

    private static void AppendAudit(SqliteConnection connection, SqliteTransaction transaction, string tokenId, string eventType, ObservationTokenScope scope, ObservationTokenAuditContext context, string? reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observation_token_audit (token_id, event_type, occurred_at_utc, workspace_id, session_id, issuer_id, trace_id, workflow_id, actor_id, reason)
            VALUES ($tokenId, $eventType, $occurredAt, $workspaceId, $sessionId, $issuerId, $traceId, $workflowId, $actorId, $reason);
            """;
        command.Parameters.AddWithValue("$tokenId", tokenId);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        AddScope(command, scope);
        command.Parameters.AddWithValue("$traceId", context.TraceId);
        command.Parameters.AddWithValue("$workflowId", context.WorkflowId);
        command.Parameters.AddWithValue("$actorId", context.ActorId);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string GetRejectionReason(SqliteConnection connection, SqliteTransaction transaction, string tokenId, ObservationTokenScope scope, string? resourcePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT resource_path, workspace_id, session_id, issuer_id, expires_at_utc, status FROM observation_tokens WHERE token_id = $tokenId;";
        command.Parameters.AddWithValue("$tokenId", tokenId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return "Observation token is invalid.";
        if (!StringComparer.Ordinal.Equals(reader.GetString(1), scope.WorkspaceId)) return "Observation token was issued for a different workspace.";
        if (!StringComparer.Ordinal.Equals(reader.GetString(2), scope.SessionId)) return "Observation token was issued for a different session.";
        if (!StringComparer.Ordinal.Equals(reader.GetString(3), scope.IssuerId)) return "Observation token was issued by a different issuer.";
        if (!string.IsNullOrEmpty(resourcePath) && !StringComparer.Ordinal.Equals(reader.GetString(0), resourcePath)) return "Observation token path mismatch.";
        if (DateTimeOffset.Parse(reader.GetString(4)) <= DateTimeOffset.UtcNow) return "Observation token has expired.";
        return reader.GetString(5) == "revoked" ? "Observation token has been revoked." : "Observation token has already been consumed.";
    }
}
