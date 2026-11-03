using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

#pragma warning disable AMPELUM_V09_PREVIEW

public sealed class StreamingChatSessionTests
{
    static AmpelumContext BaseCtx(IChatClient chat, ITraceSink? sink = null) =>
        new(chat, new ChatAdapter(), sink ?? NullTraceSink.Instance, NullLoggerFactory.Instance);

    // ── basic send ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_YieldsTokens_And_WritesTrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("Hello, ", "world!");
        var sink = new CaptureSink();
        var session = new StreamingChatSession(BaseCtx(chat, sink), "You are helpful.");

        var tokens = new List<string>();
        await foreach (var t in session.SendAsync("Hi", ct))
            tokens.Add(t);

        tokens.ShouldBe(new[] { "Hello, ", "world!" });
        sink.Written.Count.ShouldBe(1);
        sink.Written[0].ModuleName.ShouldBe("StreamingChatSession");
        sink.Written[0].Error.ShouldBeNull();
        sink.Written[0].RawResponse.ShouldBe("Hello, world!");
    }

    [Fact]
    public async Task SendAsync_MultipleMessages_AccumulatesSessionTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient()
            .EnqueueStreamingChunks("A")
            .EnqueueStreamingChunks("B");
        var session = new StreamingChatSession(BaseCtx(chat), "Sys");

        await foreach (var _ in session.SendAsync("first", ct)) { }
        var firstTotal = session.SessionTotal;
        await foreach (var _ in session.SendAsync("second", ct)) { }

        session.SessionTotal.InputTokens.ShouldBeGreaterThanOrEqualTo(firstTotal.InputTokens);
        session.SessionTotal.OutputTokens.ShouldBeGreaterThanOrEqualTo(firstTotal.OutputTokens);
    }

    [Fact]
    public async Task SendAsync_WithUsageInChunk_CapturesToLastUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new UsageReportingClient(
            chunks: ["Hi"],
            usage: new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });
        var session = new StreamingChatSession(BaseCtx(chat), "Sys");

        await foreach (var _ in session.SendAsync("Hey", ct)) { }

        session.LastUsage!.InputTokens.ShouldBe(10L);
        session.LastUsage!.OutputTokens.ShouldBe(5L);
        session.SessionTotal.InputTokens.ShouldBe(10L);
    }

    [Fact]
    public async Task SendAsync_WithRates_ComputesCost()
    {
        var ct = TestContext.Current.CancellationToken;
        var rates = new ModelRateTable(InputPerToken: 0.000001m, OutputPerToken: 0.000003m);
        var chat = new UsageReportingClient(
            chunks: ["response"],
            usage: new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 500 });
        var session = new StreamingChatSession(BaseCtx(chat), "Sys", rates: rates);

        await foreach (var _ in session.SendAsync("query", ct)) { }

        var expected = 1000 * 0.000001m + 500 * 0.000003m;
        session.LastUsage!.EstimatedCostUsd.ShouldBe(expected);
    }

    // ── hooks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_GivesTheAfterHooksTheWholeReplyAsOutputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("Hello, ", "streamed ", "world!");
        var hook = new OutputsCapturingHook();
        var session = new StreamingChatSession(BaseCtx(chat).WithHook(hook), "Sys");

        await foreach (var _ in session.SendAsync("hi", ct)) { }

        hook.Outputs.ShouldNotBeNull();
        hook.Outputs!["text"].ShouldBe("Hello, streamed world!");
    }

    [Fact]
    public async Task SendAsync_AfterHookThatRejectsTheReply_FailsTheTurnAfterTheTextWasSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("bad ", "reply");
        var session = new StreamingChatSession(BaseCtx(chat).WithHook(new RejectingAfterHook()), "Sys");
        var tokens = new List<string>();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var t in session.SendAsync("hi", ct)) tokens.Add(t);
        });

        tokens.ShouldBe(["bad ", "reply"]);
    }

    [Fact]
    public async Task SendAsync_WithHooks_FiresOnBeforeAndOnAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("ok");
        var hook = new RecordingHook();
        var session = new StreamingChatSession(BaseCtx(chat).WithHook(hook), "Sys");

        await foreach (var _ in session.SendAsync("hello", ct)) { }

        hook.BeforeCount.ShouldBe(1);
        hook.AfterCount.ShouldBe(1);
        hook.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendAsync_StreamError_FiresOnError_Rethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var hook = new RecordingHook();
        var sink = new CaptureSink();
        var chat = new ErrorStreamingClient(new InvalidOperationException("stream failed"));
        var session = new StreamingChatSession(
            new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance) { Hooks = [hook] },
            "Sys");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("x", ct)) { }
        });

        hook.ErrorCount.ShouldBe(1);
        hook.AfterCount.ShouldBe(0);
        sink.Written.Count.ShouldBe(1);
        sink.Written[0].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task SendAsync_OnBeforeThrows_FiresOnError_Rethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("ok");
        var hook = new ThrowingBeforeHook();
        var session = new StreamingChatSession(BaseCtx(chat).WithHook(hook), "Sys");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("x", ct)) { }
        });

        hook.ErrorFired.ShouldBeTrue();
    }

    // ── tool calls ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ToolCall_DispatchesAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var toolInvoked = false;
        var tool = AIFunctionFactory.Create(
            () => { toolInvoked = true; return "tool-result"; },
            "my_tool", "A test tool");
        var chat = new ToolCallThenTextClient("my_tool", "final answer");
        var session = new StreamingChatSession(BaseCtx(chat), "Sys", tools: [tool]);

        var tokens = new List<string>();
        await foreach (var t in session.SendAsync("do it", ct))
            tokens.Add(t);

        toolInvoked.ShouldBeTrue();
        tokens.ShouldContain("final answer");
    }

    [Fact]
    public async Task SendAsync_UnknownTool_SendsErrorResultAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new ToolCallThenTextClient("unknown_tool", "done");
        var session = new StreamingChatSession(BaseCtx(chat), "Sys", tools: []);

        var tokens = new List<string>();
        await foreach (var t in session.SendAsync("go", ct))
            tokens.Add(t);

        tokens.ShouldContain("done");
    }

    // ── resume ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithPriorHistory_IncludesItInPrompt()
    {
        var ct = TestContext.Current.CancellationToken;
        var priorHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "earlier question"),
            new(ChatRole.Assistant, "earlier answer"),
        };
        var chat = new FakeChatClient().EnqueueStreamingChunks("continuation");
        var session = new StreamingChatSession(BaseCtx(chat), "Sys", priorHistory: priorHistory);

        await foreach (var _ in session.SendAsync("continue", ct)) { }

        // system + 2 prior history + 1 new user = 4 messages sent
        chat.RecordedCalls[0].Messages.Count.ShouldBe(4);
    }

    [Fact]
    public async Task History_ExposesConversationMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("reply");
        var session = new StreamingChatSession(BaseCtx(chat), "Sys");

        await foreach (var _ in session.SendAsync("hello", ct)) { }

        // system + user + assistant
        session.History.Count.ShouldBe(3);
        session.History[0].Role.ShouldBe(ChatRole.System);
        session.History[1].Role.ShouldBe(ChatRole.User);
        session.History[2].Role.ShouldBe(ChatRole.Assistant);
    }

    // ── test helpers ─────────────────────────────────────────────────────────

    sealed class CaptureSink : ITraceSink
    {
        public List<TraceRecord> Written { get; } = new();
        public void Write(TraceRecord r) => Written.Add(r);
    }

    sealed class RecordingHook : IHook
    {
        public int BeforeCount;
        public int AfterCount;
        public int ErrorCount;
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) { BeforeCount++; return default; }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) { AfterCount++; return default; }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) { ErrorCount++; return default; }
    }

    sealed class OutputsCapturingHook : IHook
    {
        public IReadOnlyDictionary<string, object?>? Outputs;
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) { Outputs = ctx.Outputs; return default; }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    sealed class RejectingAfterHook : IHook
    {
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) =>
            ctx.Outputs is null ? default : ValueTask.FromException(new InvalidOperationException("rejected"));
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    sealed class ThrowingBeforeHook : IHook
    {
        public bool ErrorFired;
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) =>
            ValueTask.FromException(new InvalidOperationException("before failed"));
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        { ErrorFired = true; return default; }
    }

    sealed class UsageReportingClient(string[] chunks, UsageDetails usage) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test");
        public object? GetService(Type t, object? k) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var c in chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, c);
            }
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new UsageContent(usage)] };
        }
    }

    sealed class ErrorStreamingClient(Exception ex) : IChatClient
    {
        public ChatClientMetadata Metadata => new("test");
        public object? GetService(Type t, object? k) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw ex;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    // First call: FunctionCallContent; second call: plain text.
    sealed class ToolCallThenTextClient(string toolName, string finalText) : IChatClient
    {
        int _calls;
        public ChatClientMetadata Metadata => new("test");
        public object? GetService(Type t, object? k) => null;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (_calls++ == 0)
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>())]
                };
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, finalText);
        }
    }
}
