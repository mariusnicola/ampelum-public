using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Context;

/// <summary>SQLite-backed IEventStore with WAL mode, per-turn seq counters, and in-process writer semaphore.</summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _writerSem = new(1, 1);
    private int _turnCounter;

    /// <summary>Maximum serialized event body size before EventTooLargeException is thrown.</summary>
    public const int MaxEventBytes = 256 * 1024;

    private SqliteEventStore(SqliteConnection c) { _conn = c; }

    /// <summary>Opens or creates a SQLite event store at the given path.</summary>
    public static async ValueTask<SqliteEventStore> OpenAsync(string dbPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await c.OpenAsync(ct);
        foreach (var pragma in new[] { "journal_mode=WAL", "synchronous=NORMAL", "wal_autocheckpoint=1000", "foreign_keys=ON" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragma};";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await InitSchemaAsync(c, ct);
        var store = new SqliteEventStore(c);
        store._turnCounter = await store.LoadTurnCounterAsync(ct);
        return store;
    }

    internal static async Task InitSchemaAsync(SqliteConnection c, CancellationToken ct)
    {
        const string sql = """
        CREATE TABLE IF NOT EXISTS events(
          key TEXT PRIMARY KEY,
          turn INTEGER NOT NULL,
          seq INTEGER NOT NULL,
          type TEXT NOT NULL,
          ts TEXT NOT NULL,
          content_hash TEXT,
          tool_key TEXT,
          chapter_id TEXT,
          superseded_by TEXT,
          body_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_turn ON events(turn);
        CREATE INDEX IF NOT EXISTS idx_turn_type ON events(turn, type);
        CREATE INDEX IF NOT EXISTS idx_content_hash ON events(content_hash);
        CREATE INDEX IF NOT EXISTS idx_tool_key ON events(tool_key);
        CREATE INDEX IF NOT EXISTS idx_chapter_id ON events(chapter_id);
        CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS seq_per_turn(turn INTEGER PRIMARY KEY, next_seq INTEGER NOT NULL);
        """;
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> LoadTurnCounterAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM counters WHERE name='turn'";
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is long l ? (int)l : 0;
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireWriterLockAsync(CancellationToken ct)
    {
        if (!await _writerSem.WaitAsync(0, ct))
            throw new InvalidOperationException("Writer lock already held by another caller in this process.");
        return new WriterRelease(_writerSem);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(EventEntry entry, CancellationToken ct = default)
    {
        if (_writerSem.CurrentCount != 0)
            throw new InvalidOperationException("AppendAsync requires writer lock.");
        var json = JsonSerializer.Serialize(entry, EventLogJsonContext.Default.EventEntry);
        if (json.Length > MaxEventBytes)
            throw new EventTooLargeException(json.Length, MaxEventBytes);

        string? contentHash = entry is ToolResult tr ? tr.ContentHashHex : null;
        string? toolKey = entry switch { ToolCall tc => tc.Name, ToolResult tr2 => tr2.ToolCallKey, _ => null };
        string? chapterId = entry is ChapterBoundary cb ? cb.ChapterId : entry is SummaryEntry se ? se.ChapterId : null;

        using var tx = (SqliteTransaction)await _conn.BeginTransactionAsync(ct);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO events(key,turn,seq,type,ts,content_hash,tool_key,chapter_id,superseded_by,body_json)
            VALUES($k,$t,$s,$ty,$ts,$ch,$tk,$ci,$sb,$bj);
            """;
            cmd.Parameters.AddWithValue("$k", entry.Key);
            cmd.Parameters.AddWithValue("$t", entry.Turn);
            cmd.Parameters.AddWithValue("$s", entry.Seq);
            cmd.Parameters.AddWithValue("$ty", entry.GetType().Name);
            cmd.Parameters.AddWithValue("$ts", entry.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$ch", (object?)contentHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tk", (object?)toolKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ci", (object?)chapterId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sb", (object?)entry.SupersededBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bj", json);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (entry is UserMessage)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
            INSERT INTO counters(name,value) VALUES('turn',1)
            ON CONFLICT(name) DO UPDATE SET value=value+1;
            """;
            await cmd.ExecuteNonQueryAsync(ct);
            Interlocked.Increment(ref _turnCounter);
        }
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEntry> ReadAsync(EventQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        var sql = "SELECT body_json FROM events WHERE 1=1";
        if (query.SinceTurn is int s) { sql += " AND turn>=$s"; cmd.Parameters.AddWithValue("$s", s); }
        if (query.UntilTurn is int u) { sql += " AND turn<=$u"; cmd.Parameters.AddWithValue("$u", u); }
        if (!query.IncludeSuperseded) sql += " AND superseded_by IS NULL";
        if (query.ContentHashPrefix is string hp) { sql += " AND content_hash LIKE $hp"; cmd.Parameters.AddWithValue("$hp", hp + "%"); }
        if (query.ToolKeyPrefix is string tp) { sql += " AND tool_key LIKE $tp"; cmd.Parameters.AddWithValue("$tp", tp + "%"); }
        sql += " ORDER BY turn ASC, seq ASC";
        cmd.CommandText = sql;
        using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var json = rd.GetString(0);
            var ev = JsonSerializer.Deserialize(json, EventLogJsonContext.Default.EventEntry);
            if (ev is null) continue;
            if (query.Types is { Length: > 0 } && Array.IndexOf(query.Types, ev.GetType().Name) < 0) continue;
            yield return ev;
        }
    }

    /// <inheritdoc />
    public ValueTask<int> CurrentTurnAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Volatile.Read(ref _turnCounter));

    /// <inheritdoc />
    public async ValueTask<int> NextSeqAsync(int turn, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
        INSERT INTO seq_per_turn(turn,next_seq) VALUES($t,1)
        ON CONFLICT(turn) DO UPDATE SET next_seq=next_seq+1
        RETURNING next_seq;
        """;
        cmd.Parameters.AddWithValue("$t", turn);
        var r = await cmd.ExecuteScalarAsync(ct);
        return (int)((long)r! - 1);
    }

    /// <inheritdoc />
    public async ValueTask SupersedeAsync(string key, string supersededBy, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE events SET superseded_by=$by WHERE key=$k";
        cmd.Parameters.AddWithValue("$by", supersededBy);
        cmd.Parameters.AddWithValue("$k", key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask CheckpointAsync(CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
        _writerSem.Dispose();
    }

    private sealed class WriterRelease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        public WriterRelease(SemaphoreSlim s) { _sem = s; }
        public ValueTask DisposeAsync() { _sem.Release(); return ValueTask.CompletedTask; }
    }
}