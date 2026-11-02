#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using System.Data;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class HostSqliteOpenLatencyTests
{
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
