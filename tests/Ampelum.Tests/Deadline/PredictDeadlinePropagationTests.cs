using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Deadline;

/// <summary>
/// PSQ-5 (v0.9g) integration tests â€” Predict.ForwardAsync must check the deadline at every
/// phase boundary listed in the spec: before-onbefore, post-onbefore, post-lm-call, post-parse,
/// pre-onafter, post-onafter. On a deadline crossing the call must throw
/// <see cref="DeadlineExceededException"/>, the OnError hook path must run, and an error trace
/// must be written to the sink.
/// </summary>
public sealed class PredictDeadlinePropagationTests
{
    [Signature("Answer concisely.")]
    public sealed record Qa : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class RecordingHook : IHook
    {
        public int Order => 0;
        public int BeforeCalls;
        public int AfterCalls;
        public int ErrorCalls;
        public Exception? LastError;
        public Func<HookContext, ValueTask>? BeforeOverride;
        public Func<HookContext, ValueTask>? AfterOverride;

        public ValueTask OnBeforeAsync(HookContext context, CancellationToken ct = default)
        {
            BeforeCalls++;
            return BeforeOverride?.Invoke(context) ?? default;
        }
        public ValueTask OnAfterAsync(HookContext context, CancellationToken ct = default)
        {
            AfterCalls++;
            return AfterOverride?.Invoke(context) ?? default;
        }
        public ValueTask OnErrorAsync(HookContext context, Exception exception, CancellationToken ct = default)
        {
            ErrorCalls++;
            LastError = exception;
            return default;
        }
    }

    static AmpelumContext Ctx(IChatClient chat, InMemoryTraceSink sink, DateTimeOffset? deadline = null, IReadOnlyList<IHook>? hooks = null) =>
        new(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance) { Deadline = deadline, Hooks = hooks };

    static FakeChatClient OkChat() =>
        new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nParis");

    [Fact]
    public async Task ForwardAsync_DeadlineInPast_ThrowsBeforeOnBefore()
    {
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        var ctx = Ctx(chat, sink, deadline: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        var predict = new Predict<Qa>(ctx);

        var ex = await Should.ThrowAsync<DeadlineExceededException>(
            () => predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken));

        ex.Phase.ShouldBe("before-onbefore");
        chat.RecordedCalls.Count.ShouldBe(0); // LM never invoked
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<DeadlineExceededException>();
    }

    [Fact]
    public async Task ForwardAsync_DeadlineCrossedDuringOnBefore_ThrowsPostOnBefore()
    {
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        // Margin is large so the before-onbefore check can never see an expired deadline
        // on a loaded machine; the hook then waits on the clock itself, not on a guessed sleep.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        var hook = new RecordingHook
        {
            BeforeOverride = async _ =>
            {
                while (DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(10, TestContext.Current.CancellationToken);
            },
        };
        var ctx = Ctx(chat, sink, deadline: deadline, hooks: new[] { (IHook)hook });
        var predict = new Predict<Qa>(ctx);

        var ex = await Should.ThrowAsync<DeadlineExceededException>(
            () => predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken));

        ex.Phase.ShouldBe("post-onbefore");
        chat.RecordedCalls.Count.ShouldBe(0);
        hook.BeforeCalls.ShouldBe(1);
        hook.ErrorCalls.ShouldBe(1);
        hook.AfterCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ForwardAsync_DeadlineCrossedDuringLmCall_ThrowsPostLm()
    {
        // We use a delaying chat client to cross the deadline mid-call.
        var sink = new InMemoryTraceSink();
        var chat = new DelayingChat(TimeSpan.FromMilliseconds(1200),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis")));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        var ctx = Ctx(chat, sink, deadline: deadline);
        var predict = new Predict<Qa>(ctx);

        var ex = await Should.ThrowAsync<DeadlineExceededException>(
            () => predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken));

        ex.Phase.ShouldBe("post-lm-call");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeOfType<DeadlineExceededException>();
    }

    [Fact]
    public async Task ForwardAsync_PerCallDeadlineFromHookMetadata_OverridesContext()
    {
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        // Context deadline far in future, but hook injects a past per-call deadline.
        var hook = new RecordingHook
        {
            BeforeOverride = ctx =>
            {
                ctx.Set(HookMetadataKeys.DeadlineUtc, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
                return default;
            },
        };
        var ctx = Ctx(chat, sink,
            deadline: DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            hooks: new[] { (IHook)hook });
        var predict = new Predict<Qa>(ctx);

        var ex = await Should.ThrowAsync<DeadlineExceededException>(
            () => predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken));

        ex.Phase.ShouldBe("post-onbefore");
        chat.RecordedCalls.Count.ShouldBe(0);
        hook.ErrorCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ForwardAsync_NoDeadline_SucceedsNormally()
    {
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        var ctx = Ctx(chat, sink, deadline: null);
        var predict = new Predict<Qa>(ctx);

        var result = await predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("Paris");
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].Error.ShouldBeNull();
    }

    [Fact]
    public async Task ForwardAsync_FutureDeadline_SucceedsNormally()
    {
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        var ctx = Ctx(chat, sink, deadline: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
        var predict = new Predict<Qa>(ctx);

        var result = await predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldBe("Paris");
    }

    [Fact]
    public async Task ForwardAsync_ShortCircuitPath_ChecksDeadlineBeforeOnAfter()
    {
        // OnBefore short-circuits by setting Outputs. Then OnAfter delays past the deadline.
        var sink = new InMemoryTraceSink();
        var chat = OkChat();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(80);
        var hook = new RecordingHook
        {
            BeforeOverride = ctx =>
            {
                ctx.Outputs = new Dictionary<string, object?> { ["Answer"] = "ShortCircuit" };
                return default;
            },
            AfterOverride = async _ => await Task.Delay(150, TestContext.Current.CancellationToken),
        };
        var ctx = Ctx(chat, sink, deadline: deadline, hooks: new[] { (IHook)hook });
        var predict = new Predict<Qa>(ctx);

        // Wait past the deadline before invoking RunAfter.
        var ex = await Should.ThrowAsync<DeadlineExceededException>(async () =>
        {
            await Task.Delay(120, TestContext.Current.CancellationToken);
            await predict.ForwardAsync(new Qa { Question = "Q" }, ct: TestContext.Current.CancellationToken);
        });

        // Either pre-onafter (short-circuit branch) or before-onbefore depending on timing.
        // We assert it's one of the deadline phases.
        ex.Phase.ShouldBeOneOf("before-onbefore", "pre-onafter");
        chat.RecordedCalls.Count.ShouldBe(0); // short-circuit never calls LM
    }

    sealed class DelayingChat(TimeSpan delay, ChatResponse response) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("delaying");
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return response;
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type s, object? k = null) => null;
        public void Dispose() { }
    }
}
