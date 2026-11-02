using Microsoft.Data.Sqlite;

namespace SignalGrid.Ampelum.Agent.CodeIntelligence;

/// <summary>
/// Persistent symbol index backed by SQLite in WAL mode.
/// WAL allows concurrent reads while a single writer serialized by _writeLock updates the DB.
/// Every write is a transaction — partial state never survives a crash.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private const int CurrentSchemaVersion = 3;

    private readonly SqliteConnection _conn;
    private readonly object _writeLock = new();
    private bool _disposed;

    public event Action? OnIndexUpdated;

    // ── In-memory embedding cache (avoids GetAllEmbeddings() allocating 73MB per query) ─

    private readonly ReaderWriterLockSlim _cacheLock = new();
    private (Symbol Symbol, float[] Embedding)[]? _cachedEmbeddings;
    private bool _cacheDirty = true;

    public int TotalSymbols => (int)Scalar<long>("SELECT COUNT(*) FROM symbols");
    public int FileCount    => (int)Scalar<long>("SELECT COUNT(DISTINCT file) FROM symbols");
    public IndexStats GetStats() => new(TotalSymbols, FileCount);

    /// <summary>Get all embeddings from cache, rebuilding on write if dirty.</summary>
    public (Symbol Symbol, float[] Embedding)[] GetCachedEmbeddings()
    {
        if (_cacheDirty)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                if (_cacheDirty) // double-check
                {
                    _cachedEmbeddings = LoadAllEmbeddings();
                    _cacheDirty = false;
                }
            }
            finally { _cacheLock.ExitWriteLock(); }
        }

        _cacheLock.EnterReadLock();
        try { return _cachedEmbeddings ?? []; }
        finally { _cacheLock.ExitReadLock(); }
    }

    private (Symbol Symbol, float[] Embedding)[] LoadAllEmbeddings()
    {
        var results = new List<(Symbol, float[])>();
        using var cmd = Cmd("SELECT * FROM symbols WHERE embedding IS NOT NULL");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sym = Read(r);
            if (r["embedding"] is byte[] { Length: > 0 } blob)
                results.Add((sym, EmbeddingEngine.BytesToFloatArray(blob)));
        }
        return [.. results];
    }

    private void InvalidateCache()
    {
        _cacheLock.EnterWriteLock();
        try { _cacheDirty = true; }
        finally { _cacheLock.ExitWriteLock(); }
    }

    public IndexStore(string workingDir)
    {
        var dir = Path.Combine(workingDir, ".ampelum");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");

        _conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");

        if (!IntegrityCheck())
        {
            _conn.Close();
            File.Delete(dbPath);
            _conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            _conn.Open();
            Exec("PRAGMA journal_mode=WAL");
            Exec("PRAGMA synchronous=NORMAL");
        }

        InitSchema();
    }

    // ── writes ────────────────────────────────────────────────────────────────

    public void UpsertFile(string file, Symbol[] symbols)
    {
        lock (_writeLock)
        {
            using var tx = _conn.BeginTransaction();
            DeleteFileTx(file, tx);
            foreach (var s in symbols) InsertTx(s, tx);
            tx.Commit();
        }
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    public void BatchUpsert(params (string File, Symbol[] Symbols)[] updates)
    {
        lock (_writeLock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (file, symbols) in updates)
            {
                DeleteFileTx(file, tx);
                foreach (var s in symbols) InsertTx(s, tx);
            }
            tx.Commit();
        }
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    public void RemoveFile(string file)
    {
        lock (_writeLock)
        {
            using var cmd = Cmd("DELETE FROM symbols WHERE file=@f");
            cmd.Parameters.AddWithValue("@f", file);
            cmd.ExecuteNonQuery();
        }
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    public void UpdateSummary(string file, string name, int lineStart, string? summary)
    {
        lock (_writeLock)
        {
            using var cmd = Cmd(
                "UPDATE symbols SET summary=@s WHERE file=@f AND name=@n AND line_start=@l");
            cmd.Parameters.AddWithValue("@s",  (object?)summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f",  file);
            cmd.Parameters.AddWithValue("@n",  name);
            cmd.Parameters.AddWithValue("@l",  lineStart);
            cmd.ExecuteNonQuery();
        }
        InvalidateCache();
    }

    public void UpdateEmbedding(string file, string name, int lineStart, byte[]? embedding)
    {
        lock (_writeLock)
        {
            using var cmd = Cmd(
                "UPDATE symbols SET embedding=@e WHERE file=@f AND name=@n AND line_start=@l");
            cmd.Parameters.AddWithValue("@e",  (object?)embedding ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f",  file);
            cmd.Parameters.AddWithValue("@n",  name);
            cmd.Parameters.AddWithValue("@l",  lineStart);
            cmd.ExecuteNonQuery();
        }
        InvalidateCache();
    }

    public void Clear()
    {
        lock (_writeLock) Exec("DELETE FROM symbols");
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    // ── code_edges writes ─────────────────────────────────────────────────

    /// <summary>Replace all edges for a file (delete old, insert new).</summary>
    public void UpsertEdges(string file, CodeGraphEdge[] edges)
    {
        lock (_writeLock)
        {
            using var tx = _conn.BeginTransaction();

            // Delete existing edges for this source file
            using (var del = Cmd("DELETE FROM code_edges WHERE src_file=@f"))
            {
                del.Transaction = tx;
                del.Parameters.AddWithValue("@f", file);
                del.ExecuteNonQuery();
            }

            foreach (var e in edges)
            {
                using var ins = Cmd("""
                    INSERT OR REPLACE INTO code_edges
                        (src_file, src_line, src_name, tgt_file, tgt_line, tgt_name, edge_type, source_line)
                    VALUES (@sf, @sl, @sn, @tf, @tl, @tn, @et, @sourcel)
                    """);
                ins.Transaction = tx;
                ins.Parameters.AddWithValue("@sf", e.SrcFile);
                ins.Parameters.AddWithValue("@sl", e.SrcLine);
                ins.Parameters.AddWithValue("@sn", e.SrcName);
                ins.Parameters.AddWithValue("@tf", (object?)e.TgtFile ?? DBNull.Value);
                ins.Parameters.AddWithValue("@tl", (object?)e.TgtLine ?? DBNull.Value);
                ins.Parameters.AddWithValue("@tn", e.TgtName);
                ins.Parameters.AddWithValue("@et", e.EdgeType.ToString());
                ins.Parameters.AddWithValue("@sourcel", (object?)e.SourceLine ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    /// <summary>Delete all code edges.</summary>
    public void ClearEdges()
    {
        lock (_writeLock) Exec("DELETE FROM code_edges");
        InvalidateCache();
        OnIndexUpdated?.Invoke();
    }

    // ── reads (WAL allows concurrent reads without blocking the writer) ────────

    public Symbol[] GetAllSymbols() =>
        ReadSymbols("SELECT * FROM symbols ORDER BY file, line_start");

    public Symbol[] GetFileSymbols(string file) =>
        ReadSymbols("SELECT * FROM symbols WHERE file=@f ORDER BY line_start",
            ("@f", (object)file));

    public (Symbol Symbol, float[] Embedding)[] GetAllEmbeddings()
    {
        var results = new List<(Symbol, float[])>();
        using var cmd = Cmd("SELECT * FROM symbols WHERE embedding IS NOT NULL");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sym = Read(r);
            if (r["embedding"] is byte[] { Length: > 0 } blob)
                results.Add((sym, EmbeddingEngine.BytesToFloatArray(blob)));
        }
        return [.. results];
    }

    public bool IntegrityCheck()
    {
        try
        {
            using var cmd = Cmd("PRAGMA integrity_check");
            return cmd.ExecuteScalar() as string == "ok";
        }
        catch { return false; }
    }

    // ── code_edges reads ──────────────────────────────────────────────────

    /// <summary>All edges where this symbol name is the source.</summary>
    public CodeGraphEdge[] GetEdgesBySrc(string name)
    {
        using var cmd = Cmd("SELECT * FROM code_edges WHERE src_name=@n COLLATE NOCASE");
        cmd.Parameters.AddWithValue("@n", name);
        return ReadEdges(cmd);
    }

    /// <summary>All edges where this symbol name is the target.</summary>
    public CodeGraphEdge[] GetEdgesByTgt(string name)
    {
        using var cmd = Cmd("SELECT * FROM code_edges WHERE tgt_name=@n COLLATE NOCASE");
        cmd.Parameters.AddWithValue("@n", name);
        return ReadEdges(cmd);
    }

    /// <summary>All edges involving a given file (as source).</summary>
    public CodeGraphEdge[] GetFileEdges(string file)
    {
        using var cmd = Cmd("SELECT * FROM code_edges WHERE src_file=@f");
        cmd.Parameters.AddWithValue("@f", file);
        return ReadEdges(cmd);
    }

    private static CodeGraphEdge[] ReadEdges(SqliteCommand cmd)
    {
        var results = new List<CodeGraphEdge>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new CodeGraphEdge(
                SrcFile:    (string)r["src_file"],
                SrcLine:    r["src_line"] is DBNull ? 0 : (int)(long)r["src_line"],
                SrcName:    (string)r["src_name"],
                TgtFile:    r["tgt_file"] as string,
                TgtLine:    r["tgt_line"] is DBNull ? null : (int?)(long)r["tgt_line"],
                TgtName:    (string)r["tgt_name"],
                EdgeType:   Enum.TryParse<EdgeType>((string)r["edge_type"], out var et) ? et : EdgeType.References,
                SourceLine: r["source_line"] is DBNull ? null : (int?)(long)r["source_line"]));
        }
        return [.. results];
    }

    // ── schema ────────────────────────────────────────────────────────────────

    private void InitSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);

        var stored = Scalar<string?>("SELECT value FROM meta WHERE key='schema_version'");
        if (stored != CurrentSchemaVersion.ToString())
        {
            Exec("DROP TABLE IF EXISTS symbols");
            Exec($"INSERT OR REPLACE INTO meta VALUES ('schema_version','{CurrentSchemaVersion}')");
        }

        Exec("""
            CREATE TABLE IF NOT EXISTS symbols (
                id           INTEGER PRIMARY KEY,
                file         TEXT    NOT NULL,
                line_start   INTEGER NOT NULL,
                line_end     INTEGER NOT NULL,
                kind         TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                signature    TEXT,
                summary      TEXT,
                embedding    BLOB,
                content_hash TEXT    NOT NULL,
                indexed_at   INTEGER NOT NULL,
                UNIQUE(file, line_start, name)
            )
            """);

        Exec("CREATE INDEX IF NOT EXISTS idx_file ON symbols(file)");
        Exec("CREATE INDEX IF NOT EXISTS idx_name ON symbols(name COLLATE NOCASE)");

        // ── code_edges table (added in schema v3) ────────────────────────────
        Exec("""
            CREATE TABLE IF NOT EXISTS code_edges (
                id           INTEGER PRIMARY KEY,
                src_file     TEXT    NOT NULL,
                src_line     INTEGER,
                src_name     TEXT    NOT NULL,
                tgt_file     TEXT,
                tgt_line     INTEGER,
                tgt_name     TEXT    NOT NULL,
                edge_type    TEXT    NOT NULL,
                source_line  INTEGER,
                UNIQUE(src_file, src_name, tgt_name, edge_type)
            )
            """);
        Exec("CREATE INDEX IF NOT EXISTS idx_edge_src ON code_edges(src_name COLLATE NOCASE)");
        Exec("CREATE INDEX IF NOT EXISTS idx_edge_tgt ON code_edges(tgt_name COLLATE NOCASE)");
        Exec("CREATE INDEX IF NOT EXISTS idx_edge_file ON code_edges(src_file)");
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private void DeleteFileTx(string file, SqliteTransaction tx)
    {
        using var cmd = Cmd("DELETE FROM symbols WHERE file=@f");
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("@f", file);
        cmd.ExecuteNonQuery();
    }

    private void InsertTx(Symbol s, SqliteTransaction tx)
    {
        using var cmd = Cmd("""
            INSERT OR REPLACE INTO symbols
              (file,line_start,line_end,kind,name,signature,summary,embedding,content_hash,indexed_at)
            VALUES (@f,@ls,@le,@k,@n,@sig,@sum,@emb,@hash,@at)
            """);
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("@f",    s.File);
        cmd.Parameters.AddWithValue("@ls",   s.LineStart);
        cmd.Parameters.AddWithValue("@le",   s.LineEnd);
        cmd.Parameters.AddWithValue("@k",    s.Kind.ToString());
        cmd.Parameters.AddWithValue("@n",    s.Name);
        cmd.Parameters.AddWithValue("@sig",  (object?)s.Signature  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sum",  (object?)s.Summary     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb",  (object?)s.Embedding   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", s.ContentHash);
        cmd.Parameters.AddWithValue("@at",   s.IndexedAt.Ticks);
        cmd.ExecuteNonQuery();
    }

    private Symbol[] ReadSymbols(string sql, params (string Key, object Value)[] parameters)
    {
        var results = new List<Symbol>();
        using var cmd = Cmd(sql);
        foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(Read(r));
        return [.. results];
    }

    private static Symbol Read(SqliteDataReader r) => new(
        File:        (string)r["file"],
        LineStart:   (int)(long)r["line_start"],
        LineEnd:     (int)(long)r["line_end"],
        Kind:        Enum.TryParse<SymbolKind>((string)r["kind"], out var k) ? k : SymbolKind.Function,
        Name:        (string)r["name"],
        Signature:   r["signature"] as string ?? "",
        Summary:     r["summary"] as string,
        ContentHash: (string)r["content_hash"],
        Embedding:   r["embedding"] as byte[],
        IndexedAt:   new DateTime((long)r["indexed_at"], DateTimeKind.Utc)
    );

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default!;
        return (T)Convert.ChangeType(v, typeof(T));
    }

    private SqliteCommand Cmd(string sql)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Close();
        _conn.Dispose();
    }
}

public sealed record IndexStats(int TotalSymbols, int FileCount);
