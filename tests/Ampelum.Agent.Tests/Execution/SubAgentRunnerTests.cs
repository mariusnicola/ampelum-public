using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent.Execution;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Execution;

public sealed class SubAgentRunnerTests
{
    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add($"error:{error.GetType().Name}");
            return default;
        }
    }

    static SubAgentTask MakeTask(string description = "do the thing") =>
        new() { Id = "t1", Description = description };

    [Fact]
    public async Task RunAsync_NoAmbientContext_CallsChatDirectlyAndCompletes()
    {
        var chat = new FakeChatClient().EnqueueText("DONE: finished the task");
        var runner = new SubAgentRunner(chat, workingDir: ".", sessionId: "s1");

        var result = await runner.RunAsync(MakeTask(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Outcome.ShouldBe("finished the task");
        chat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_AmbientContextWithHooks_RunsHookPipelineButStillUsesInjectedChatClient()
    {
        // A different chat client sits behind the ambient context; SubAgentRunner must still call
        // through its own _chatClient, not the ambient one.
        var ambientChat = new FakeChatClient();
        var subagentChat = new FakeChatClient().EnqueueText("DONE: used my own client");
        var hook = new SpyHook();
        var ambientContext = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        var runner = new SubAgentRunner(subagentChat, workingDir: ".", sessionId: "s2");

        SubAgentResult result;
        using (AmpelumContext.Push(ambientContext))
        {
            result = await runner.RunAsync(MakeTask(), TestContext.Current.CancellationToken);
        }

        result.Success.ShouldBeTrue();
        result.Outcome.ShouldBe("used my own client");
        subagentChat.RecordedCalls.Count.ShouldBe(1);
        ambientChat.RecordedCalls.ShouldBeEmpty();
        hook.Calls.ShouldBe(new[] { "before:SubAgentRunner.Turn", "after:SubAgentRunner.Turn" });
    }

    [Fact]
    public async Task RunAsync_LmFailure_IsCaughtAndReturnedAsUnsuccessfulResult()
    {
        var chat = new FakeChatClient().EnqueueError(new InvalidOperationException("provider down"));
        var runner = new SubAgentRunner(chat, workingDir: ".", sessionId: "s3");

        var result = await runner.RunAsync(MakeTask(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Outcome.ShouldContain("provider down");
    }
}
