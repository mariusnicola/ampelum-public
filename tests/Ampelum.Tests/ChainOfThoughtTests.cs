using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class ChainOfThoughtTests
{
    [Signature("Answer with reasoning.")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class ScriptedChat(ChatResponse response) : IChatClient
    {
        public int Calls;
        public IReadOnlyList<ChatMessage>? LastMessages;
        public ChatClientMetadata Metadata { get; } = new("scripted");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default)
        {
            Calls++; LastMessages = m.ToList();
            return Task.FromResult(response);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ForwardAsync_ParsesReasoningAndAnswer()
    {
        var text = "[[ ## reasoning ## ]]\nBecause Paris is capital\n[[ ## Answer ## ]]\nParis";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), new InMemoryTraceSink(), NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);

        var r = await cot.ForwardAsync(new Qa { Question = "Capital of France?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");
        r.Reasoning().ShouldBe("Because Paris is capital");
    }

    [Fact]
    public async Task ChainOfThought_WritesReasoning_UnderTraceKeysReasoningKey()
    {
        var text = "[[ ## reasoning ## ]]\nstep 1\nstep 2\n[[ ## Answer ## ]]\nok";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);
        var pred = await cot.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        pred.Trace.Outputs.ShouldContainKey(TraceKeys.Reasoning);
        var reasoning = pred.Trace.Outputs[TraceKeys.Reasoning]?.ToString() ?? "";
        reasoning.ShouldContain("step 1");
    }

    [Fact]
    public async Task ForwardAsync_ReasoningPromptMarker_InSystemMessage()
    {
        var text = "[[ ## reasoning ## ]]\nx\n[[ ## Answer ## ]]\ny";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);
        await cot.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);

        chat.LastMessages![0].Text.ShouldContain("[[ ## reasoning ## ]]");  // reasoning marker present
    }

    [Fact]
    public async Task ForwardAsync_AutoInjectsSyntheticReasoningIntoDemos()
    {
        var text = "[[ ## reasoning ## ]]\nsome reasoning\n[[ ## Answer ## ]]\nHello";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);

        var demo = new Example(
            new Dictionary<string, object?> { ["Question"] = "?" },
            new Dictionary<string, object?> { ["Answer"] = "!" });

        await cot.ForwardAsync(new Qa { Question = "x" }, demos: [demo], ct: TestContext.Current.CancellationToken);

        // The rendered messages include the synthetic reasoning marker in the demo block.
        string.Join("\n", chat.LastMessages!.Select(m => m.Text)).ShouldContain("[[ ## reasoning ## ]]");
    }

    [Fact]
    public async Task ForwardAsync_NullDemos_TreatsAsEmpty()
    {
        var text = "[[ ## reasoning ## ]]\nx\n[[ ## Answer ## ]]\ny";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);
        await cot.ForwardAsync(new Qa { Question = "?" }, demos: null, ct: TestContext.Current.CancellationToken);
        chat.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ForwardAsync_WithJsonAdapter_StructuredOutput()
    {
        // JsonAdapter is IStructuredOutputAdapter. The AugmentingAdapter must forward
        // BuildResponseFormat calls to the inner adapter's schema for the AUGMENTED signature.
        var body = """{"reasoning":"bc Paris","answer":"Paris"}""";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        var ctx = new AmpelumContext(chat, new JsonAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);
        var r = await cot.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Paris");
        r.Reasoning().ShouldBe("bc Paris");
    }

    [Fact]
    public async Task ForwardAsync_FormatResponseViaAugmentingAdapter_RoundTrips()
    {
        // Exercise the ITestableAdapter.FormatResponse path through the wrapped adapter —
        // used by FakeChatClient.EnqueueOutputs in Task 21.
        // The augmenting adapter should delegate to the inner ChatAdapter and include the
        // synthetic reasoning field in the formatted response.
        var text = "[[ ## reasoning ## ]]\nThinking\n[[ ## Answer ## ]]\nFoo";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);
        var r = await cot.ForwardAsync(new Qa { Question = "?" }, ct: TestContext.Current.CancellationToken);
        r.Value.Answer.ShouldBe("Foo");
    }

    [Fact]
    public void Constructor_NullContext_Throws() =>
        Should.Throw<ArgumentNullException>(() => new ChainOfThought<Qa>(null!));

    // ── Coverage: FormatResponse happy path and throw path ───────────────────

    /// <summary>An adapter that implements only IAdapter (no ITestableAdapter).</summary>
    sealed class BareAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<Example> demos) =>
            new ChatAdapter().Format(s, inputs, demos);
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) =>
            new ChatAdapter().Parse(s, r);
    }

    [Fact]
    public async Task AugmentedAdapter_FormatResponse_HappyPath_DelegatesToInnerTestableAdapter()
    {
        // AugmentingAdapter.FormatResponse delegates to the inner ITestableAdapter when present.
        var text = "[[ ## reasoning ## ]]\nok\n[[ ## Answer ## ]]\nX";
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);

        // Cast to ITestableAdapter — AugmentingAdapter implements it.
        var testable = (ITestableAdapter)cot.AugmentedAdapter;
        var meta = SignatureMetadataCache.For<Qa>();
        var response = testable.FormatResponse(meta, new Dictionary<string, object?>
        {
            ["reasoning"] = "thinking",
            ["Answer"] = "Y"
        });
        response.Text.ShouldContain("Y");
    }

    [Fact]
    public void AugmentedAdapter_FormatResponse_ThrowsWhenInnerIsNotTestableAdapter()
    {
        // AugmentingAdapter.FormatResponse throws when the inner adapter doesn't implement ITestableAdapter.
        var chat = new ScriptedChat(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        var ctx = new AmpelumContext(chat, new BareAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var cot = new ChainOfThought<Qa>(ctx);

        var testable = (ITestableAdapter)cot.AugmentedAdapter;
        var meta = SignatureMetadataCache.For<Qa>();
        Should.Throw<InvalidOperationException>(() =>
            testable.FormatResponse(meta, new Dictionary<string, object?>
            {
                ["reasoning"] = "x",
                ["Answer"] = "y"
            }));
    }
}
