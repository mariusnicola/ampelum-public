#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using System.Data;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class HostSqliteOpenLatencyTests
{
    [Fact]
    public async Task Open_AfterTheFirstOne_DoesNotWriteToTheDatabase_SoAWriterHoldingTheLockDoesNotDelayIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using (factory.Open()) { }
        using var writer = factory.Open();
        var transaction = writer.BeginTransaction();
        using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO prompt_versions(version_id, module_name, status, created_at, file_path) "
                + "VALUES('v-lock', 'm', 'challenger', '2026-01-01T00:00:00Z', 'f');";
            insert.ExecuteNonQuery();
        }

        // The readiness probe opens like this; it must not wait for, or collide with, a writer that is busy.
        var open = Task.Run(() => factory.OpenWithLatencyBudget(TimeSpan.FromSeconds(5)).Dispose(), ct);
        var first = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10), ct));
        transaction.Rollback();

        first.ShouldBeSameAs(open);
        await open;
    }

    [Fact]
    public void Open_AfterTheDatabaseFileWasDeleted_CreatesTheTablesAgain()
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        using (factory.Open()) { }
        // Let go of the file, then delete it with its journal files, as an operator would.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(fix.SqlitePath + suffix)) File.Delete(fix.SqlitePath + suffix);

        using var conn = factory.Open();

        File.Exists(fix.SqlitePath).ShouldBeTrue();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompt_versions;";
        Convert.ToInt64(cmd.ExecuteScalar()).ShouldBe(0L);
    }

    [Theory]
    [InlineData(10, 50, true)]
    [InlineData(40, 50, true)]
    [InlineData(49, 50, true)]
    [InlineData(51, 50, false)]
    [InlineData(80, 50, false)]
    public void Latency_BudgetEnforced(int simulatedMs, int budgetMs, bool shouldSucceed)
    {
        using var fix = new TempStorageFixture();
        var factory = TestData.Factory(fix.Options);
        if (shouldSucceed)
        {
            using var conn = factory.OpenSimulateLatency(
                TimeSpan.FromMilliseconds(simulatedMs), TimeSpan.FromMilliseconds(budgetMs));
            conn.State.ShouldBe(ConnectionState.Open);
        }
        else
        {
            Should.Throw<SqliteOpenError>(() =>
                factory.OpenSimulateLatency(
                    TimeSpan.FromMilliseconds(simulatedMs), TimeSpan.FromMilliseconds(budgetMs)));
        }
    }
}
