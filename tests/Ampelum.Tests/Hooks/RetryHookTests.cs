using SignalGrid.Ampelum.Hooks;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Hooks;

public sealed class RetryHookTests
{
    static HookContext NewCtx() =>
        new() { ModuleName = "Predict", Inputs = new Dictionary<string, object?>() };

    [Fact]
    public void Order_Is100()
    {
        new RetryHook().Order.ShouldBe(100);
    }

    [Fact]
    public async Task OnBefore_OnAfter_AreNoop()
    {
        var h = new RetryHook();
        var ctx = NewCtx();
        await h.OnBeforeAsync(ctx, TestContext.Current.CancellationToken);
        await h.OnAfterAsync(ctx, TestContext.Current.CancellationToken);
        ctx.Metadata.ContainsKey("retry.attempt").ShouldBeFalse();
    }

    [Fact]
    public async Task OnError_RecordsAttempt_AndDelay()
    {
        var h = new RetryHook(maxAttempts: 3,
            baseDelay: TimeSpan.FromMilliseconds(200),
            maxDelay: TimeSpan.FromSeconds(5),
            rng: new Random(42));
        var ctx = NewCtx();

        await h.OnErrorAsync(ctx, new Exception("boom"), TestContext.Current.CancellationToken);

        ctx.Metadata.ContainsKey("retry.attempt").ShouldBeTrue();
        ((int)ctx.Metadata["retry.attempt"]!).ShouldBe(1);
        ctx.Metadata.ContainsKey("retry.next_delay_ms").ShouldBeTrue();
        var d = (int)ctx.Metadata["retry.next_delay_ms"]!;
        d.ShouldBeGreaterThanOrEqualTo(200);
        d.ShouldBeLessThan(5_000);
    }

    [Fact]
    public async Task OnError_BackoffGrows_Capped()
    {
        var h = new RetryHook(maxAttempts: 10,
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromMilliseconds(1000),
            rng: new Random(0));
        var ctx = NewCtx();

        await h.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);
        await h.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);
        await h.OnErrorAsync(ctx, new Exception(), TestContext.Current.CancellationToken);

        ((int)ctx.Metadata["retry.attempt"]!).ShouldBe(3);
        var d = (int)ctx.Metadata["retry.next_delay_ms"]!;
        d.ShouldBeLessThanOrEqualTo(1100);
    }
}
