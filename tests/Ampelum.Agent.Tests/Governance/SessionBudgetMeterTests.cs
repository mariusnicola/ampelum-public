using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Agent.Execution;
using SignalGrid.Ampelum.Testing;
using System.Text.Json;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SessionBudgetMeterTests
{
    [Fact]
    public void TryReserveCall_LetsThroughUpToMaxCallCount_ThenRefusesAndCountsTheRefusals()
    {
        var meter = new SessionBudgetMeter(new SessionBudget { MaxCallCount = 2 });

        meter.TryReserveCall().ShouldBeTrue();
        meter.TryReserveCall().ShouldBeTrue();
        meter.TryReserveCall().ShouldBeFalse();
        meter.TryReserveCall().ShouldBeFalse();

        meter.Calls.ShouldBe(2);
        meter.Refused.ShouldBe(2);
    }

    [Fact]
    public void TokensAndOutputBytes_CountAgainstTheirLimits()
    {
        var tokens = new SessionBudgetMeter(new SessionBudget { MaxTokens = 100 });
        tokens.TryReserveCall().ShouldBeTrue();
        tokens.AddTokens(100);
        tokens.IsExhausted.ShouldBeTrue();
        tokens.TryReserveCall().ShouldBeFalse();

        var bytes = new SessionBudgetMeter(new SessionBudget { MaxOutputBytes = 10 });
        bytes.AddOutputBytes(9);
        bytes.IsExhausted.ShouldBeFalse();
        bytes.AddOutputBytes(1);
        bytes.IsExhausted.ShouldBeTrue();
        bytes.OutputBytes.ShouldBe(10);
    }

    [Fact]
    public void NoLimits_NeverRefuses()
    {
        var meter = new SessionBudgetMeter(new SessionBudget());

        for (var i = 0; i < 100; i++) meter.TryReserveCall().ShouldBeTrue();

        meter.Refused.ShouldBe(0);
    }
}

public sealed class ToolDispatcherBudgetTests
{
    static readonly AgentIdentity Caller = new("budget-test", "budget-session", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    static (ToolDispatcher Dispatcher, InMemoryToolAuditLog Audit, Func<int> Handled) Create(SessionBudgetMeter meter, string output)
    {
        var loader = new InMemoryToolLoader();
        loader.Register(new ToolManifest { Name = "read", Description = "read", DangerTier = DangerTier.Safe, Reversibility = Reversibility.Reversible });
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit, budget: meter);
        var handled = 0;
        dispatcher.RegisterHandler("read", (_, _) =>
        {
            handled++;
            return Task.FromResult(new ToolResult(true, output, null, PolicyDecision.Allowed));
        });
        return (dispatcher, audit, () => handled);
    }

    static readonly IReadOnlyDictionary<string, JsonElement> NoArgs = new Dictionary<string, JsonElement>();

    [Fact]
    public async Task CallBeyondMaxCallCount_IsRefusedAuditedAndNeverReachesTheHandler()
    {
        var meter = new SessionBudgetMeter(new SessionBudget { MaxCallCount = 1 });
        var (dispatcher, audit, handled) = Create(meter, "ok");

        var first = await dispatcher.InvokeAsync("read", NoArgs, Caller);
        var second = await dispatcher.InvokeAsync("read", NoArgs, Caller);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeFalse();
        second.Error.ShouldNotBeNull().ShouldContain("budget");
        handled().ShouldBe(1);
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldContain("budget.exhausted");
    }

    [Fact]
    public async Task OutputBytesOfEarlierResults_CountAgainstMaxOutputBytes()
    {
        var meter = new SessionBudgetMeter(new SessionBudget { MaxOutputBytes = 10 });
        var (dispatcher, _, handled) = Create(meter, "0123456789ABC");

        var first = await dispatcher.InvokeAsync("read", NoArgs, Caller);
        var second = await dispatcher.InvokeAsync("read", NoArgs, Caller);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeFalse();
        meter.OutputBytes.ShouldBe(13);
        handled().ShouldBe(1);
    }

    [Fact]
    public async Task ABlockedCall_DoesNotUseUpTheBudget()
    {
        var meter = new SessionBudgetMeter(new SessionBudget { MaxCallCount = 1 });
        var (dispatcher, _, handled) = Create(meter, "ok");

        (await dispatcher.InvokeAsync("missing", NoArgs, Caller)).Success.ShouldBeFalse();
        (await dispatcher.InvokeAsync("read", NoArgs, Caller)).Success.ShouldBeTrue();

        handled().ShouldBe(1);
        meter.Refused.ShouldBe(0);
    }
}

public sealed class SubAgentRunnerBudgetTests
{
    static SubAgentTask Task1() => new() { Id = "t1", Description = "do the thing" };

    [Fact]
    public async Task RunBeyondMaxCallCount_IsRefusedWithoutCallingTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new SessionBudgetMeter(new SessionBudget { MaxCallCount = 1 });
        var chat = new FakeChatClient().EnqueueText("DONE: first").EnqueueText("DONE: second");
        var runner = new SubAgentRunner(chat, ".", "s1", budget: meter);

        var first = await runner.RunAsync(Task1(), ct);
        var second = await runner.RunAsync(Task1(), ct);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeFalse();
        second.Outcome.ShouldContain("budget");
        chat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TokensOfATurn_StopTheRunBeforeTheNextTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new SessionBudgetMeter(new SessionBudget { MaxTokens = 50 });
        var chat = new FakeChatClient()
            .EnqueueText("still working", new UsageDetails { InputTokenCount = 40, OutputTokenCount = 20 })
            .EnqueueText("DONE: never reached");
        var runner = new SubAgentRunner(chat, ".", "s1", maxTurns: 5, budget: meter);

        var result = await runner.RunAsync(Task1(), ct);

        result.Success.ShouldBeFalse();
        result.Outcome.ShouldContain("budget");
        chat.RecordedCalls.Count.ShouldBe(1);
        meter.Tokens.ShouldBe(60);
    }
}
