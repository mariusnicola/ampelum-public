#pragma warning disable AMPELUM_V09_PREVIEW
using Microsoft.Data.Sqlite;
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using System.Data;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class HostSqliteSchemaTests
{
    private static IEnumerable<string> QueryTableNames(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));
        return tables;
    }

    [Fact]
    public void EnsureCreated_OnEmptyDb_CreatesAllTables()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using var conn = factory.Open();
        var tables = QueryTableNames(conn).ToList();
        tables.ShouldContain("audit_index");
        tables.ShouldContain("prompt_versions");
        tables.ShouldContain("optimizer_runs");
        tables.ShouldContain("module_registry");
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using (factory.Open()) { }
        using (factory.Open()) { }
    }

    [Fact]
    public void OpenWithLatencyBudget_BelowBudget_Returns()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using var conn = factory.OpenWithLatencyBudget(TimeSpan.FromSeconds(2));
        conn.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public void OpenWithLatencyBudget_OverBudget_ThrowsSqliteOpenError()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        Should.Throw<SqliteOpenError>(() =>
            factory.OpenSimulateLatency(TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void StatusCheckConstraint_RejectsUnknownStatus()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO prompt_versions(version_id, module_name, status, created_at, file_path) "
          + "VALUES('v1','M','BOGUS','2026-04-27T00:00:00Z','/p')";
        Should.Throw<SqliteException>(() => cmd.ExecuteNonQuery());
    }
}
