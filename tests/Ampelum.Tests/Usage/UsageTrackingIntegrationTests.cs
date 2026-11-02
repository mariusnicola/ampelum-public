using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// Integration tests verifying that AmpelumContext.Usage is populated by Predict (forward + streaming) and ReAct.
/// </summary>
[Collection(ActivityListenerCollection.Name)]
public sealed class UsageTrackingIntegrationTests
{
    // â”€â”€ Shared signatures â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Signature("")]
    sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("")]
    sealed record QaReact : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    static AmpelumContext Ctx(IChatClient chat, IUsageTracker? tracker = null) =>
        new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        with { Usage = tracker };

    // â”€â”€ Predict.ForwardAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Predict_ForwardAsync_WithUsageTracker_RecordsRequest()
    {
        var usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 };
        var fake = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nParis", usage);
        var tracker = new DefaultUsageTracker();
        var predict = new Predict<Qa>(Ctx(fake, tracker));
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        var snap = tracker.Snapshot();
        snap.RequestCount.ShouldBe(1);
        snap.PromptTokens.ShouldBe(5);
        snap.CompletionTokens.ShouldBe(3);
    }

    [Fact]
    public async Task Predict_ForwardAsync_NullUsageTracker_DoesNotThrow()
    {
        var fake = new FakeChatClient().EnqueueText("[[ ## Answer ## ]]\nParis");
        var predict = new Predict<Qa>(Ctx(fake, tracker: null));
        // Should complete without NullReferenceException
        var result = await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        result.Value.Answer.ShouldBe("Paris");
    }

    [Fact]
    public async Task Predict_ForwardAsync_CacheHit_RecordsCacheHit()
    {
        // Arrange: build a ChatResponse that has the cache-hit annotation
        var responseMsg = new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis"))
        {
            Usage = new UsageDetails { InputTokenCount = 2, OutputTokenCount = 1 },
            AdditionalProperties = new AdditionalPropertiesDictionary { ["SignalGrid.Ampelum.cache_hit"] = (object)true }
        };
        var fake = new FakeChatClient().EnqueueRaw(responseMsg);
        var tracker = new DefaultUsageTracker();
        var predict = new Predict<Qa>(Ctx(fake, tracker));
        await predict.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        tracker.Snapshot().CacheHits.ShouldBe(1);
    }

    // â”€â”€ Predict.StreamAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Predict_StreamAsync_WithUsageTracker_RecordsRequest()
    {
        var usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 4 };
        var chat = new UsageStreamingChatClient(usage);
        var tracker = new DefaultUsageTracker();
        var predict = new Predict<Qa>(Ctx(chat, tracker));
        await foreach (var _ in predict.StreamAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken)) { }

        var snap = tracker.Snapshot();
        snap.RequestCount.ShouldBe(1);
        snap.PromptTokens.ShouldBe(7);
        snap.CompletionTokens.ShouldBe(4);
    }

    [Fact]
    public async Task Predict_StreamAsync_NullUsageTracker_DoesNotThrow()
    {
        var usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 };
        var chat = new UsageStreamingChatClient(usage);
        var predict = new Predict<Qa>(Ctx(chat, tracker: null));
        // Should complete without NullReferenceException
        await foreach (var _ in predict.StreamAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken)) { }
    }

    // â”€â”€ ReAct â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task ReAct_ForwardAsync_WithUsageTracker_RecordsOneLmCall()
    {
        // Single direct response (no tools called)
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 };
        var fake = new FakeChatClient().EnqueueText("final_answer: Rome", usage);
        var tracker = new DefaultUsageTracker();
        var ctx = Ctx(fake, tracker);
        var react = new ReAct<QaReact>(ctx, tools: []);
        await react.ForwardAsync(new QaReact { Question = "Where is Rome?" }, ct: TestContext.Current.CancellationToken);

        var snap = tracker.Snapshot();
        snap.RequestCount.ShouldBe(1);
        snap.PromptTokens.ShouldBe(10);
        snap.CompletionTokens.ShouldBe(5);
    }

    [Fact]
    public async Task ReAct_ForwardAsync_ForcedFinalCall_RecordsForcedCallUsage()
    {
        // Force iteration limit: 2 tool-call responses + 1 forced-final response
        // All 3 LM responses should be recorded.
        var toolCallResponse = MakeFunctionCallResponse("echo", new Dictionary<string, object?> { ["input"] = "test" });
        var forcedUsage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 8 };

        var fake = new FakeChatClient()
            .EnqueueRaw(toolCallResponse)
            .EnqueueText("final_answer: done", forcedUsage);

        var tracker = new DefaultUsageTracker();
        var ctx = new AmpelumContext(fake, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance, null)
            with { Usage = tracker };

        var echoTool = AIFunctionFactory.Create((string input) => $"echoed:{input}", "echo", "Echo a string.");
        var react = new ReAct<QaReact>(ctx, tools: [echoTool], maxIterations: 1);
        await react.ForwardAsync(new QaReact { Question = "?" }, ct: TestContext.Current.CancellationToken);

        // First call: toolCallResponse (Usage=null), second call: forced (Usage set)
        var snap = tracker.Snapshot();
        snap.RequestCount.ShouldBe(2);
        snap.PromptTokens.ShouldBe(20);
        snap.CompletionTokens.ShouldBe(8);
    }

    [Fact]
    public async Task ReAct_ForwardAsync_NullUsageTracker_DoesNotThrow()
    {
        var fake = new FakeChatClient().EnqueueText("final_answer: ok");
        var ctx = Ctx(fake, tracker: null);
        var react = new ReAct<QaReact>(ctx, tools: []);
        var result = await react.ForwardAsync(new QaReact { Question = "?" }, ct: TestContext.Current.CancellationToken);
        result.Value.Answer.ShouldBe("ok");
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Builds a ChatResponse that contains a single FunctionCallContent (no tool result).</summary>
    static ChatResponse MakeFunctionCallResponse(string functionName, Dictionary<string, object?> args)
    {
        var msg = new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent("call-1", functionName, args)
        ]);
        return new ChatResponse(msg);
    }

    /// <summary>Streaming client that emits one field chunk followed by a UsageContent chunk.</summary>
    sealed class UsageStreamingChatClient(UsageDetails usage) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("UsageStreamingChatClient");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Emit a valid field chunk for the Qa signature
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "[[ ## Answer ## ]]\nParis");
            // Emit final chunk carrying UsageContent
            await Task.Yield();
            var usageUpdate = new ChatResponseUpdate(ChatRole.Assistant, "");
            usageUpdate.Contents.Add(new UsageContent(usage));
            yield return usageUpdate;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
