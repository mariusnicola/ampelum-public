using SignalGrid.Ampelum.Budget;
using Shouldly;
using Xunit;
using BudgetCls = SignalGrid.Ampelum.Budget.Budget;

namespace Ampelum.Tests.BudgetTests_Ns;

/// <summary>
/// Coverage for the v1.0-rc1 alpha.8 (Gap 5) <c>IBudget.TryReserve</c> + <c>Release</c> additions.
/// Validates atomic reservation semantics on the in-box <see cref="BudgetImpl"/> and the default
/// interface-method fallback for third-party implementations that have not adopted the API.
/// </summary>
public sealed class BudgetTryReserveTests
{
    [Fact]
    public void TryReserve_WithinAllLimits_ReturnsTrue()
    {
        var b = BudgetCls.Create().WithTokenLimit(1000).WithCallLimit(10).WithCostLimit(50m).Build();
        b.TryReserve(calls: 1, tokens: 500, costCents: 25m).ShouldBeTrue();
    }

    [Fact]
    public void TryReserve_NoLimitsConfigured_AlwaysReturnsTrue()
    {
        var b = BudgetCls.Create().Build();
        b.TryReserve(calls: 1_000_000, tokens: 1_000_000_000L, costCents: 1_000_000m).ShouldBeTrue();
    }

    [Fact]
    public void TryReserve_OverTokenLimit_ReturnsFalse_AndDoesNotMutateState()
    {
        var b = BudgetCls.Create().WithTokenLimit(100).Build();
        b.TryReserve(1, 500, 0m).ShouldBeFalse();
        // Subsequent fitting reservation must succeed â†’ proves no leakage on the failed call.
        b.TryReserve(1, 50, 0m).ShouldBeTrue();
    }

    [Fact]
    public void TryReserve_OverCallLimit_ReturnsFalse()
    {
        var b = BudgetCls.Create().WithCallLimit(1).Build();
        b.TryReserve(2, 0, 0m).ShouldBeFalse();
    }

    [Fact]
    public void TryReserve_OverCostLimit_ReturnsFalse()
    {
        var b = BudgetCls.Create().WithCostLimit(1m).Build();
        b.TryReserve(1, 0, 5m).ShouldBeFalse();
    }

    [Fact]
    public void TryReserve_PastDeadline_ReturnsFalse()
    {
        var b = BudgetCls.Create().WithDeadline(TimeSpan.FromMilliseconds(1)).Build();
        Thread.Sleep(20);
        b.TryReserve(1, 0, 0m).ShouldBeFalse();
    }

    [Fact]
    public void TryReserve_AccumulatesAcrossCalls()
    {
        var b = BudgetCls.Create().WithCallLimit(3).Build();
        b.TryReserve(1, 0, 0m).ShouldBeTrue();
        b.TryReserve(1, 0, 0m).ShouldBeTrue();
        b.TryReserve(1, 0, 0m).ShouldBeTrue();
        b.TryReserve(1, 0, 0m).ShouldBeFalse();
    }

    [Fact]
    public void Release_RestoresHeadroom()
    {
        var b = BudgetCls.Create().WithCallLimit(2).Build();
        b.TryReserve(1, 100, 1m).ShouldBeTrue();
        b.TryReserve(1, 100, 1m).ShouldBeTrue();
        b.TryReserve(1, 100, 1m).ShouldBeFalse();
        b.Release(1, 100, 1m);
        b.TryReserve(1, 100, 1m).ShouldBeTrue();
    }

    [Fact]
    public void TryReserveConsumeRelease_Roundtrip_NetsToActual()
    {
        // Reserve 1000 tokens, consume actual 50, release the 1000 reservation.
        // Final state must reflect 50 used, 0 reserved.
        var b = BudgetCls.Create().WithTokenLimit(2000).WithCallLimit(2).Build();
        b.TryReserve(1, 1000, 0m).ShouldBeTrue();
        b.Consume(50, 0m);
        b.Release(1, 1000, 0m);

        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(50);
        s.CallsUsed.ShouldBe(1);

        // Headroom should be intact: another reserve+consume cycle fits.
        b.TryReserve(1, 1000, 0m).ShouldBeTrue();
    }

    [Fact]
    public async Task TryReserve_ParallelN_AdmitsExactlyK()
    {
        // Atomic reservation under contention: N concurrent TryReserve(1) on CallLimit=K
        // produces exactly K trues. This is the property Pareto #11 relies on.
        const int N = 64;
        const int K = 20;
        var b = BudgetCls.Create().WithCallLimit(K).Build();
        int admitted = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, N), TestContext.Current.CancellationToken,
            (_, _) =>
            {
                if (b.TryReserve(1, 0, 0m)) Interlocked.Increment(ref admitted);
                return ValueTask.CompletedTask;
            });
        admitted.ShouldBe(K);
    }

    // -- Default interface method fallback for third-party impls -----------------------------------

    sealed class LegacyBudget : IBudget
    {
        public long? TokenLimit => 100;
        public decimal? CostCentsLimit => null;
        public int? CallLimit => 1;
        public DateTimeOffset? Deadline => null;
        public long Tokens, Calls;

        public void Consume(long tokens, decimal costCents, int calls = 1)
        {
            Interlocked.Add(ref Tokens, tokens);
            Interlocked.Add(ref Calls, calls);
        }
        public BudgetSnapshot Snapshot() => new(Tokens, TokenLimit, 0m, CostCentsLimit,
            (int)Interlocked.Read(ref Calls), CallLimit, Deadline);
    }

    [Fact]
    public void TryReserve_DefaultImpl_ChecksSnapshotOnly_True()
    {
        IBudget b = new LegacyBudget();
        b.TryReserve(1, 50, 0m).ShouldBeTrue();
    }

    [Fact]
    public void TryReserve_DefaultImpl_OverLimit_ReturnsFalse()
    {
        IBudget b = new LegacyBudget();
        b.TryReserve(1, 500, 0m).ShouldBeFalse();
    }

    [Fact]
    public void Release_DefaultImpl_IsNoop()
    {
        IBudget b = new LegacyBudget();
        // No exception, no state â€” DIM contract for legacy impls.
        b.Release(1, 100, 1m);
    }
}
