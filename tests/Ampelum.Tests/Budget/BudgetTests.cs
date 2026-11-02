using SignalGrid.Ampelum.Budget;
using Shouldly;
using Xunit;
using BudgetCls = SignalGrid.Ampelum.Budget.Budget;

namespace Ampelum.Tests.BudgetTests_Ns;

public sealed class BudgetTests
{
    [Fact]
    public void Builder_BuildsBudget_WithAllLimits()
    {
        var b = BudgetCls.Create()
            .WithTokenLimit(100)
            .WithCostLimit(cents: 500)
            .WithCallLimit(50)
            .WithDeadline(TimeSpan.FromMinutes(10))
            .Build();

        b.TokenLimit.ShouldBe(100);
        b.CostCentsLimit.ShouldBe(500m);
        b.CallLimit.ShouldBe(50);
        b.Deadline.ShouldNotBeNull();
    }

    [Fact]
    public void Builder_NoLimits_IsAllowed()
    {
        var b = BudgetCls.Create().Build();
        b.TokenLimit.ShouldBeNull();
        b.CostCentsLimit.ShouldBeNull();
        b.CallLimit.ShouldBeNull();
        b.Deadline.ShouldBeNull();
    }

    [Fact]
    public void Consume_Accumulates()
    {
        var b = BudgetCls.Create().WithTokenLimit(1000).Build();
        b.Consume(100, 5m);
        b.Consume(50, 2m);
        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(150);
        s.CostCentsUsed.ShouldBe(7m);
        s.CallsUsed.ShouldBe(2);
    }

    [Fact]
    public void Consume_TokenOverLimit_Throws()
    {
        var b = BudgetCls.Create().WithTokenLimit(100).Build();
        Should.Throw<BudgetExceededException>(() => b.Consume(101, 0m));
    }

    [Fact]
    public void Consume_CostOverLimit_Throws()
    {
        var b = BudgetCls.Create().WithCostLimit(cents: 10).Build();
        Should.Throw<BudgetExceededException>(() => b.Consume(0, 11m));
    }

    [Fact]
    public void Consume_CallOverLimit_Throws()
    {
        var b = BudgetCls.Create().WithCallLimit(2).Build();
        b.Consume(0, 0m);
        b.Consume(0, 0m);
        Should.Throw<BudgetExceededException>(() => b.Consume(0, 0m));
    }

    [Fact]
    public void Consume_IsThreadSafe()
    {
        var b = BudgetCls.Create().WithTokenLimit(long.MaxValue).Build();
        Parallel.For(0, 1000, _ => b.Consume(1, 0.01m));
        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(1000);
        s.CallsUsed.ShouldBe(1000);
        s.CostCentsUsed.ShouldBe(10m);
    }

    [Fact]
    public void Snapshot_ReflectsCurrentState()
    {
        var b = BudgetCls.Create().WithTokenLimit(50).Build();
        b.Snapshot().TokensUsed.ShouldBe(0);
        b.Consume(10, 1m);
        b.Snapshot().TokensUsed.ShouldBe(10);
    }

    [Fact]
    public void Deadline_PassedAtBuild_IsRelativeToNow()
    {
        var before = DateTimeOffset.UtcNow;
        var b = BudgetCls.Create().WithDeadline(TimeSpan.FromMinutes(5)).Build();
        b.Deadline.ShouldNotBeNull();
        b.Deadline!.Value.ShouldBeGreaterThan(before.AddMinutes(4).AddSeconds(50));
        b.Deadline.Value.ShouldBeLessThan(before.AddMinutes(5).AddSeconds(10));
    }
}
