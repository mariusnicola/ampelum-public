using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>SQLite-backed implementation of <see cref="IPromptVersionStore"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PromptVersionStore : IPromptVersionStore
{
    readonly IHostSqliteConnectionFactory _factory;

    /// <summary>Initializes the store with the given connection factory.</summary>
    public PromptVersionStore(IHostSqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListModulesAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT module_name FROM prompt_versions ORDER BY module_name;";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return ValueTask.FromResult<IReadOnlyList<string>>(list);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<PromptVersionRow>> ListByModuleAsync(string moduleName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_id, module_name, status, created_at, parent_version_id, file_path, note FROM prompt_versions WHERE module_name=@m ORDER BY created_at;";
        cmd.Parameters.AddWithValue("@m", moduleName);
        using var r = cmd.ExecuteReader();
        var list = new List<PromptVersionRow>();
        while (r.Read()) list.Add(ReadRow(r));
        return ValueTask.FromResult<IReadOnlyList<PromptVersionRow>>(list);
    }

    /// <inheritdoc/>
    public ValueTask<PromptVersionRow?> GetActiveAsync(string moduleName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_id, module_name, status, created_at, parent_version_id, file_path, note FROM prompt_versions WHERE module_name=@m AND status='active' LIMIT 1;";
        cmd.Parameters.AddWithValue("@m", moduleName);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ValueTask.FromResult<PromptVersionRow?>(null);
        return ValueTask.FromResult<PromptVersionRow?>(ReadRow(r));
    }

    /// <inheritdoc/>
    public ValueTask<PromptVersionRow?> TryGetByIdAsync(string versionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionId);
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version_id, module_name, status, created_at, parent_version_id, file_path, note FROM prompt_versions WHERE version_id=@id;";
        cmd.Parameters.AddWithValue("@id", versionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ValueTask.FromResult<PromptVersionRow?>(null);
        return ValueTask.FromResult<PromptVersionRow?>(ReadRow(r));
    }

    /// <inheritdoc/>
    public ValueTask InsertChallengerAsync(PromptVersionRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        using var conn = _factory.Open();
        Insert(conn, null, row);
        return ValueTask.CompletedTask;
    }

    static void Insert(SqliteConnection conn, SqliteTransaction? tx, PromptVersionRow row)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO prompt_versions(version_id, module_name, status, created_at, parent_version_id, file_path, note)
            VALUES(@vid, @mod, @status, @cat, @pvid, @fp, @note);
            """;
        cmd.Parameters.AddWithValue("@vid", row.VersionId);
        cmd.Parameters.AddWithValue("@status", row.Status);
        cmd.Parameters.AddWithValue("@mod", row.ModuleName);
        cmd.Parameters.AddWithValue("@cat", row.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@pvid", (object?)row.ParentVersionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", row.FilePath);
        cmd.Parameters.AddWithValue("@note", (object?)row.Note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public ValueTask<PromoteResult> PromoteAtomicallyAsync(string moduleName, string targetVersionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(targetVersionId);
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            // Check target is challenger
            using var checkCmd = conn.CreateCommand();
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT status FROM prompt_versions WHERE version_id=@id;";
            checkCmd.Parameters.AddWithValue("@id", targetVersionId);
            var status = checkCmd.ExecuteScalar() as string;
            if (status != "challenger")
                throw new PromoteConflictException(
                    $"Version '{targetVersionId}' is not a challenger (status='{status ?? "not found"}').");

            // Find and retire current active
            using var findActive = conn.CreateCommand();
            findActive.Transaction = tx;
            findActive.CommandText = "SELECT version_id FROM prompt_versions WHERE module_name=@m AND status='active' LIMIT 1;";
            findActive.Parameters.AddWithValue("@m", moduleName);
            var priorActiveId = findActive.ExecuteScalar() as string;

            if (priorActiveId is not null)
            {
                using var retireCmd = conn.CreateCommand();
                retireCmd.Transaction = tx;
                retireCmd.CommandText = "UPDATE prompt_versions SET status='retired' WHERE version_id=@id;";
                retireCmd.Parameters.AddWithValue("@id", priorActiveId);
                retireCmd.ExecuteNonQuery();
            }

            // Promote target
            using var promoteCmd = conn.CreateCommand();
            promoteCmd.Transaction = tx;
            promoteCmd.CommandText = "UPDATE prompt_versions SET status='active' WHERE version_id=@id;";
            promoteCmd.Parameters.AddWithValue("@id", targetVersionId);
            var updated = promoteCmd.ExecuteNonQuery();
            if (updated == 0) throw new PromoteConflictException(
                $"Version '{targetVersionId}' not found.");

            tx.Commit();
            return ValueTask.FromResult(new PromoteResult(targetVersionId, priorActiveId));
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask DeleteAllAsync(CancellationToken ct)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM prompt_versions;";
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ReplaceAllAsync(IReadOnlyList<PromptVersionRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var conn = _factory.Open();
        // Disposing the transaction without a commit rolls it back, so a failing row leaves the old rows.
        using var tx = conn.BeginTransaction();
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM prompt_versions;";
            delete.ExecuteNonQuery();
        }
        foreach (var row in rows)
            Insert(conn, tx, row);
        tx.Commit();
        return ValueTask.CompletedTask;
    }

    private static PromptVersionRow ReadRow(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2),
            DateTimeOffset.Parse(r.GetString(3)),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6));
}
