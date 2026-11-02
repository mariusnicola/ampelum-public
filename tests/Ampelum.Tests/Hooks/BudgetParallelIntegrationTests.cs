using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Budget;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using BudgetCls = SignalGrid.Ampelum.Budget.Budget;

namespace Ampelum.Tests.Hooks;

public sealed class BudgetParallelIntegrationTests
{
    [Signature("Echo the input.")]
    public sealed record EchoSig : ISignature
    {
        [Input] public required string Q { get; init; }
        [Output] public string A { get; init; } = "";
    }

    [Fact]
    public async Task ParallelForwards_AccumulateTokens_ThreadSafely()
    {
        const int N = 50;
        var chat = new FakeChatClient();
        for (int i = 0; i < N; i++)
            chat.EnqueueText("[[ ## A ## ]]\nx",
                new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });

        var budget = BudgetCls.Create().WithTokenLimit(long.MaxValue).Build();
        var hook = new BudgetHook(budget);
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            { Hooks = new[] { (IHook)hook } };
        var predict = new Predict<EchoSig>(ctx);

        var ct = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, N).Select(i =>
            predict.ForwardAsync(new EchoSig { Q = i.ToString() }, ct: ct)).ToArray();
        await Task.WhenAll(tasks);

        var s = budget.Snapshot();
        s.TokensUsed.ShouldBe(N * 15L);
        s.CallsUsed.ShouldBe(N);
    }

    [Fact]
    public async Task ParallelForwards_TripBudget_EventuallyThrowExceeded()
    {
        const int N = 30;
        var chat = new FakeChatClient();
        for (int i = 0; i < N; i++)
            chat.EnqueueText("[[ ## A ## ]]\nx",
                new UsageDetails { InputTokenCount = 10, OutputTokenCount = 0 });

        // alpha.8 (Gap 5): BudgetHook now reserves expected tokens pre-LM via TryReserve.
        // Use a tight reservation default (10 tokens prompt + 0 completion) so reservations
        // succeed until the consumed total trips the 50-token limit, preserving the original
        // "some succeed / some fail" semantics under the new pre-LM enforcement contract.
        var budget = BudgetCls.Create().WithTokenLimit(50).Build();
        var hook = new BudgetHook(budget, scopes: null,
            defaultExpectedPromptTokens: 10, defaultExpectedCompletionTokens: 0);
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            { Hooks = new[] { (IHook)hook } };
        var predict = new Predict<EchoSig>(ctx);
        var ct = TestContext.Current.CancellationToken;

        int succeeded = 0, exceeded = 0;
        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            try
            {
                await predict.ForwardAsync(new EchoSig { Q = i.ToString() }, ct: ct);
                Interlocked.Increment(ref succeeded);
            }
            catch (BudgetExceededException) { Interlocked.Increment(ref exceeded); }
        });
        await Task.WhenAll(tasks);

        succeeded.ShouldBeGreaterThan(0);
        exceeded.ShouldBeGreaterThan(0);
        (succeeded + exceeded).ShouldBe(N);
    }
}
