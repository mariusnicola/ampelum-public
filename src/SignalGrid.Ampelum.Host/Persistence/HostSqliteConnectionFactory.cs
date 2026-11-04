using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Opens SQLite connections against the host database, creating schema on first open.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class HostSqliteConnectionFactory : IHostSqliteConnectionFactory
{
    readonly string _connectionString;
    readonly string _path;
    readonly object _schemaGate = new();
    bool _schemaReady;

    /// <summary>Initializes the factory from host options.</summary>
    public HostSqliteConnectionFactory(IOptions<AmpelumHostOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.Value.Storage.SqlitePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _path = path;
        _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared;";
    }

    /// <inheritdoc/>
    public SqliteConnection Open()
    {
        if (!File.Exists(_path))
        {
            // The database was deleted (or never made): this open creates it empty, so its tables are made again, and
            // pooled connections to the old file are let go.
            using (var stale = new SqliteConnection(_connectionString))
                SqliteConnection.ClearPool(stale);
            Volatile.Write(ref _schemaReady, false);
        }
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        HostSqliteSchema.ApplyConnectionSettings(conn);
        // The schema writes to the database, so only the first open does it; a later open that also wrote would
        // wait on any writer that is busy, and the readiness probe times an open.
        if (!Volatile.Read(ref _schemaReady))
            lock (_schemaGate)
                if (!_schemaReady)
                {
                    HostSqliteSchema.EnsureCreated(conn);
                    _schemaReady = true;
                }
        return conn;
    }

    /// <inheritdoc/>
    public SqliteConnection OpenWithLatencyBudget(TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        var conn = Open();
        sw.Stop();
        if (sw.Elapsed > budget)
        {
            conn.Dispose();
            throw new SqliteOpenError(
                $"SQLite open took {sw.ElapsedMilliseconds}ms, exceeding budget of {budget.TotalMilliseconds}ms.");
        }
        return conn;
    }

    /// <summary>Test seam: opens with a simulated latency, then checks budget.</summary>
    internal SqliteConnection OpenSimulateLatency(TimeSpan simulated, TimeSpan budget)
    {
        if (simulated > budget)
            throw new SqliteOpenError(
                $"Simulated latency {simulated.TotalMilliseconds}ms exceeds budget {budget.TotalMilliseconds}ms.");
        var conn = Open();
        return conn;
    }
}
