using SignalGrid.Ampelum.Budget;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.BudgetTests_Ns;

public sealed class BudgetExceededExceptionTests
{
    [Fact]
    public void Carries_Snapshot_AndMessage()
    {
        var snap = new BudgetSnapshot(
            TokensUsed: 100, TokenLimit: 50,
            CostCentsUsed: 0m, CostCentsLimit: null,
            CallsUsed: 1, CallLimit: null,
            Deadline: null);
        var ex = new BudgetExceededException(snap);
        ex.Snapshot.ShouldBe(snap);
        ex.Message.ShouldContain("Budget exceeded");
    }

    [Fact]
    public void IsInvalidOperationException()
    {
        var ex = new BudgetExceededException(new BudgetSnapshot(0, null, 0m, null, 0, null, null));
        ex.ShouldBeAssignableTo<InvalidOperationException>();
    }
}
