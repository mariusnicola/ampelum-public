using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Hooks.Internal;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Hooks;

/// <summary>An after hook that throws must not stop the lower-order after hooks, AuditHook in particular.</summary>
public sealed class HookPipelineAfterFailureTests
{
    [Signature("Answer the question.")]
    public sealed record Ask : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class ThrowingAfterHook(int order, string message = "boom") : IHook
    {
        public int Order => order;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => throw new InvalidOperationException(message);
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    sealed class RecordingHook(int order, List<string> log) : IHook
    {
        public int Order => order;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) { log.Add("after:" + order); return default; }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    static HookContext Ctx() => new() { ModuleName = "M", Inputs = new Dictionary<string, object?>() };

    static async Task<List<AuditEntry>> EntriesAsync(InMemoryAuditSink sink)
    {
        var list = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Every_after_hook_runs_when_a_higher_order_one_throws_and_the_exception_is_rethrown()
    {
        var log = new List<string>();
        var pipeline = HookPipeline.GetOrCreate([new RecordingHook(10, log), new RecordingHook(100, log), new ThrowingAfterHook(300), new RecordingHook(500, log)]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => pipeline.RunAfterAsync(Ctx(), TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldBe("boom");
        log.ShouldBe(["after:500", "after:100", "after:10"]);
    }

    [Fact]
    public async Task One_exception_keeps_its_type_and_two_become_an_AggregateException_in_run_order()
    {
        var log = new List<string>();
        var pipeline = HookPipeline.GetOrCreate([new RecordingHook(10, log), new ThrowingAfterHook(100, "low"), new ThrowingAfterHook(300, "high")]);

        var ex = await Should.ThrowAsync<AggregateException>(
            () => pipeline.RunAfterAsync(Ctx(), TestContext.Current.CancellationToken).AsTask());

        ex.InnerExceptions.Select(e => e.Message).ShouldBe(["high", "low"]);
        log.ShouldBe(["after:10"]);
    }

    [Fact]
    public async Task Predict_still_audits_a_call_whose_after_hook_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var chat = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nyes\n[[ ## completed ## ]]");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink))
            .WithHook(new ThrowingAfterHook(300, "moderated"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new Predict<Ask>(ctx).ForwardAsync(new Ask { Question = "q" }, ct: ct));

        ex.Message.ShouldBe("moderated");
        var entries = await EntriesAsync(sink);
        entries.Count.ShouldBe(1);
        entries[0].Outputs["Answer"].ShouldBe("yes");
    }

    [Fact]
    public async Task StreamingChatSession_still_audits_a_turn_whose_after_hook_throws_and_drops_the_reply_from_the_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new InMemoryAuditSink();
        var chat = new FakeChatClient().EnqueueStreamingChunks("flagged ", "reply");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink))
            .WithHook(new ThrowingAfterHook(300, "moderated"));
        var session = new StreamingChatSession(ctx, "Sys");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("hi", ct)) { }
        });

        (await EntriesAsync(sink)).Count.ShouldBe(1);
        // The turn failed: the system prompt and the user message stay, the reply does not.
        session.History.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User]);
    }

    [Fact]
    public async Task StreamingChatSession_keeps_the_reply_when_the_after_hooks_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("fine");
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new AuditHook(new InMemoryAuditSink()));
        var session = new StreamingChatSession(ctx, "Sys");

        await foreach (var _ in session.SendAsync("hi", ct)) { }

        session.History.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User, ChatRole.Assistant]);
    }
}
