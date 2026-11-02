using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;
using BudgetCls = SignalGrid.Ampelum.Budget.Budget;

namespace Ampelum.Tests.Hooks;

public sealed class BudgetHookTests
{
    static HookContext NewCtx(IDictionary<string, object?>? meta = null)
    {
        var c = new HookContext { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };
        if (meta is not null)
            foreach (var kv in meta) c.Set(kv.Key, kv.Value);
        return c;
    }

    [Fact]
    public void Order_IsZero()
    {
        new BudgetHook(BudgetCls.Create().Build()).Order.ShouldBe(0);
    }

    [Fact]
    public async Task OnAfter_ConsumesTokensAndCost_FromMetadata()
    {
        var b = BudgetCls.Create().WithTokenLimit(1000).Build();
        var h = new BudgetHook(b);
        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.PromptTokens] = 30L,
            [HookMetadataKeys.CompletionTokens] = 20L,
            [HookMetadataKeys.BudgetCostCents] = 1.5m,
        });
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(50);
        s.CostCentsUsed.ShouldBe(1.5m);
        s.CallsUsed.ShouldBe(1);
    }

    [Fact]
    public async Task OnAfter_MissingMetadata_UsesZero()
    {
        var b = BudgetCls.Create().Build();
        var h = new BudgetHook(b);
        await h.OnAfterAsync(NewCtx(), TestContext.Current.CancellationToken);
        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(0);
        s.CostCentsUsed.ShouldBe(0m);
        s.CallsUsed.ShouldBe(1);
    }

    [Fact]
    public async Task OnBefore_OpenCircuit_Throws_BeforeLm()
    {
        var b = BudgetCls.Create().WithCallLimit(1).Build();
        var h = new BudgetHook(b);
        await h.OnAfterAsync(NewCtx(), TestContext.Current.CancellationToken);
        await Should.ThrowAsync<BudgetExceededException>(() =>
            h.OnBeforeAsync(NewCtx(), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task OnBefore_WithinReservationDefaults_DoesNotThrow()
    {
        // Default reservation = 500+500=1000 tokens. Limit 2000 leaves headroom.
        var h = new BudgetHook(BudgetCls.Create().WithTokenLimit(2000).Build());
        await h.OnBeforeAsync(NewCtx(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBefore_ReservationExceedsTokenLimit_Throws()
    {
        // Default reservation = 1000 tokens; limit 100 â†’ TryReserve returns false.
        var h = new BudgetHook(BudgetCls.Create().WithTokenLimit(100).Build());
        await Should.ThrowAsync<BudgetExceededException>(() =>
            h.OnBeforeAsync(NewCtx(), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task OnBefore_RespectsExpectedTokenMetadata_OverridingDefaults()
    {
        // Caller supplies tight expected estimate â†’ reservation fits even under low limit.
        var b = BudgetCls.Create().WithTokenLimit(100).Build();
        var h = new BudgetHook(b);
        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.ExpectedPromptTokens] = 10L,
            [HookMetadataKeys.ExpectedCompletionTokens] = 5L,
        });
        await h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnBefore_RespectsExpectedCostCents()
    {
        var b = BudgetCls.Create().WithCostLimit(1m).Build();
        var h = new BudgetHook(b);
        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.ExpectedCostCents] = 5m,
        });
        await Should.ThrowAsync<BudgetExceededException>(() =>
            h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task OnBeforeOnAfter_Roundtrip_ReleasesReservation_AndConsumesActual()
    {
        // Pre-LM: reserve default (1000 tokens). Post-LM: actual was 50; budget should reflect 50, not 1050.
        var b = BudgetCls.Create().WithTokenLimit(2000).WithCallLimit(2).Build();
        var h = new BudgetHook(b);
        var ctx = NewCtx();
        await h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Set(HookMetadataKeys.PromptTokens, 30L);
        ctx.Set(HookMetadataKeys.CompletionTokens, 20L);
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        var s = b.Snapshot();
        s.TokensUsed.ShouldBe(50);
        s.CallsUsed.ShouldBe(1);

        // A second cycle still fits under CallLimit=2 because reservation #1 was released.
        var ctx2 = NewCtx();
        await h.OnBeforeAsync(ctx2, TestContext.Current.CancellationToken);
        ctx2.Set(HookMetadataKeys.PromptTokens, 10L);
        ctx2.Set(HookMetadataKeys.CompletionTokens, 5L);
        await h.OnAfterAsync(ctx2, TestContext.Current.CancellationToken);

        var s2 = b.Snapshot();
        s2.CallsUsed.ShouldBe(2);
        s2.TokensUsed.ShouldBe(65);
    }

    [Fact]
    public async Task OnError_AfterReservation_RollsBackReservation()
    {
        var b = BudgetCls.Create().WithCallLimit(1).Build();
        var h = new BudgetHook(b);
        var ctx = NewCtx();
        await h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        // Simulated LM failure â†’ OnError. After release the budget is back to clean state.
        await h.OnErrorAsync(ctx, new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        // Same budget should now admit a fresh forward (reservation rolled back).
        var ctx2 = NewCtx();
        await h.OnBeforeAsync(ctx2, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnError_WithoutPriorReservation_IsNoop()
    {
        var h = new BudgetHook(BudgetCls.Create().Build());
        await h.OnErrorAsync(NewCtx(), new Exception(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScopeKey_RoutesToScopedBudget_ConsumeWithinLimit()
    {
        var global = BudgetCls.Create().WithTokenLimit(1_000_000).Build();
        var tenant7 = BudgetCls.Create().WithTokenLimit(500).Build();
        var scopes = new Dictionary<string, IBudget> { ["tenant-7"] = tenant7 };
        var h = new BudgetHook(global, scopes);

        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.BudgetScope] = "tenant-7",
            [HookMetadataKeys.PromptTokens] = 60L,
            [HookMetadataKeys.CompletionTokens] = 50L,
        });
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        tenant7.Snapshot().TokensUsed.ShouldBe(110);
        global.Snapshot().TokensUsed.ShouldBe(0);
    }

    [Fact]
    public async Task UnknownScope_FallsBackToGlobal()
    {
        var global = BudgetCls.Create().WithTokenLimit(500).Build();
        var scopes = new Dictionary<string, IBudget>();
        var h = new BudgetHook(global, scopes);

        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.BudgetScope] = "missing",
            [HookMetadataKeys.PromptTokens] = 50L,
            [HookMetadataKeys.CompletionTokens] = 25L,
        });
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        global.Snapshot().TokensUsed.ShouldBe(75);
    }

    [Fact]
    public async Task NoScopeAndNoGlobal_IsNoop()
    {
        var h = new BudgetHook(global: null, scopes: null);
        var ctx = NewCtx(new Dictionary<string, object?>
        {
            [HookMetadataKeys.PromptTokens] = 10L,
        });
        await h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ParallelN_TryReserve_AdmitsExactlyK_OthersThrow()
    {
        // Gap 5 unit-level proof: with CallLimit=K, N concurrent OnBefore calls produce
        // exactly K admits and Nâˆ’K BudgetExceeded throws â€” atomically, pre-LM.
        const int N = 64;
        const int K = 20;
        var b = BudgetCls.Create().WithCallLimit(K).Build();
        var h = new BudgetHook(b);

        int admitted = 0, refused = 0;
        var tasks = Enumerable.Range(0, N).Select(_ => Task.Run(async () =>
        {
            try
            {
                await h.OnBeforeAsync(NewCtx(), TestContext.Current.CancellationToken);
                Interlocked.Increment(ref admitted);
            }
            catch (BudgetExceededException)
            {
                Interlocked.Increment(ref refused);
            }
        }));
        await Task.WhenAll(tasks);

        admitted.ShouldBe(K);
        refused.ShouldBe(N - K);
    }
}
