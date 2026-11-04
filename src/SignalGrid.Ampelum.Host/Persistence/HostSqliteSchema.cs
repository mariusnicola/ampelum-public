using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Host.Persistence;

internal static class HostSqliteSchema
{
    internal const int SchemaVersion = 1;

    internal const string CreateAuditIndex = """
        CREATE TABLE IF NOT EXISTS audit_index (
            trace_id     TEXT PRIMARY KEY NOT NULL,
            sequence     INTEGER NOT NULL,
            timestamp    TEXT    NOT NULL,
            module       TEXT    NOT NULL,
            event_type   TEXT    NOT NULL
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_audit_index_sequence ON audit_index(sequence);
        CREATE INDEX IF NOT EXISTS ix_audit_index_module_ts ON audit_index(module, timestamp DESC);
        """;

    internal const string CreatePromptVersions = """
        CREATE TABLE IF NOT EXISTS prompt_versions (
            version_id          TEXT PRIMARY KEY NOT NULL,
            module_name         TEXT NOT NULL,
            status              TEXT NOT NULL CHECK (status IN ('active','challenger','retired')),
            created_at          TEXT NOT NULL,
            parent_version_id   TEXT,
            file_path           TEXT NOT NULL,
            note                TEXT
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_prompt_versions_module_status
            ON prompt_versions(module_name, status);
        """;

    internal const string CreateOptimizerRuns = """
        CREATE TABLE IF NOT EXISTS optimizer_runs (
            run_id              TEXT PRIMARY KEY NOT NULL,
            module_name         TEXT NOT NULL,
            optimizer_type      TEXT NOT NULL,
            state               TEXT NOT NULL CHECK (state IN ('queued','running','succeeded','failed','cancelled')),
            started_at          TEXT NOT NULL,
            completed_at        TEXT,
            trials_completed    INTEGER NOT NULL DEFAULT 0,
            trials_total        INTEGER,
            result_version_id   TEXT,
            error_message       TEXT
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_optimizer_runs_module_started
            ON optimizer_runs(module_name, started_at DESC);
        """;

    internal const string CreateModuleRegistry = """
        CREATE TABLE IF NOT EXISTS module_registry (
            module_name      TEXT PRIMARY KEY NOT NULL,
            signature_type   TEXT NOT NULL,
            supports_stream  INTEGER NOT NULL,
            registered_at    TEXT NOT NULL
        ) WITHOUT ROWID;
        """;

    /// <summary>The settings every connection needs; none of them writes to the database.</summary>
    internal static void ApplyConnectionSettings(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets the journal mode and creates the tables and the schema version; it writes, so it runs once per database.</summary>
    internal static void EnsureCreated(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        // Check/set user_version
        cmd.CommandText = "PRAGMA user_version;";
        var dbVersion = (long)(cmd.ExecuteScalar() ?? 0L);
        if (dbVersion != 0 && dbVersion != SchemaVersion)
            throw new SqliteOpenError(
                $"Schema mismatch: expected user_version={SchemaVersion}, found {dbVersion}.");

        // Create tables
        cmd.CommandText = CreateAuditIndex;
        cmd.ExecuteNonQuery();
        cmd.CommandText = CreatePromptVersions;
        cmd.ExecuteNonQuery();
        cmd.CommandText = CreateOptimizerRuns;
        cmd.ExecuteNonQuery();
        cmd.CommandText = CreateModuleRegistry;
        cmd.ExecuteNonQuery();

        // Set version
        cmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
        cmd.ExecuteNonQuery();
    }
}
