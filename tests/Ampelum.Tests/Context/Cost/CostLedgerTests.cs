using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Context.Cost;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class CostLedgerTests
{
    [Fact]
    public async Task RecordAndRead_CostEvent_Roundtrips()
    {
        await using var store = new InMemoryEventStore();
        var ledger = new CostLedger(store);
        var cost = new PerTurnCost(1, 100, 50, 10, 5, 0.001m, 0.002m, 0.0001m, 0.0005m, 0.0036m);
        await ledger.RecordAsync(1, cost, TestContext.Current.CancellationToken);

        var list = new List<PerTurnCost>();
        await foreach (var c in ledger.ReadAllAsync(TestContext.Current.CancellationToken))
            list.Add(c);

        list.Count.ShouldBe(1);
        list[0].TotalCost.ShouldBe(0.0036m);
    }
}
