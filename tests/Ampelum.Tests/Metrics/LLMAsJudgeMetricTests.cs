using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

public sealed class LLMAsJudgeMetricTests
{
    sealed record QA : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public string Answer { get; init; } = "";
    }

    // Signature with nullable string to hit null branch in Read()
    sealed record NullableSig : ISignature
    {
        [Input] public string? Question { get; init; }
        [Output] public string? Answer { get; init; }
    }

    // Signature with IFormattable (double) output to hit IFormattable branch in Read()
    sealed record NumericSig : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public double Value { get; init; }
    }

    // Signature with List<string> output to hit _ (object fallback) branch in Read()
    sealed record ListSig : ISignature
    {
        [Input] public string Question { get; init; } = "";
        [Output] public List<string> Tags { get; init; } = [];
    }

    static readonly SignatureMetadata Meta = SignatureMetadataCache.For<QA>();
    static readonly SignatureMetadata NullableMeta = SignatureMetadataCache.For<NullableSig>();
    static readonly SignatureMetadata NumericMeta = SignatureMetadataCache.For<NumericSig>();
    static readonly SignatureMetadata ListMeta = SignatureMetadataCache.For<ListSig>();

    [Fact]
    public void Score_ValidJudgeResponse_ReturnsScore()
    {
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.75, \"reasoning\": \"ok\"}");
        new LLMAsJudgeMetric<QA>(chat, "Judge")
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta).ShouldBe(0.75);
    }

    [Fact]
    public void Score_Invalid_Throws()
    {
        var chat = new FakeChatClient().EnqueueText("not json");
        Should.Throw<AdapterParseException>(() =>
            new LLMAsJudgeMetric<QA>(chat, "Judge")
                .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta));
    }

    [Fact]
    public void Score_EmitsTraceRecord_WhenSinkNonNull()
    {
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.5, \"reasoning\": \"mid\"}");
        var sink = new InMemoryTraceSink();
        new LLMAsJudgeMetric<QA>(chat, "Judge", criteria: null, traceSink: sink)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta);
        sink.Records.Count.ShouldBe(1);
        sink.Records[0].ModuleName.ShouldBe("LLMAsJudgeMetric");
        sink.Records[0].Outputs["score"].ShouldBe(0.5);
        sink.Records[0].Metadata[TraceKeys.Reasoning].ShouldBe("mid");
    }

    [Fact]
    public void Score_WithCriteria_BulletsInPrompt()
    {
        var chat = new FakeChatClient().EnqueueText("{\"score\": 1.0, \"reasoning\": \"\"}");
        var sink = new InMemoryTraceSink();
        new LLMAsJudgeMetric<QA>(chat, "Judge", criteria: new[] { "accuracy", "brevity" }, traceSink: sink)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta);
        var userMsg = sink.Records[0].PromptMessages.Last(p => p.Role == Microsoft.Extensions.AI.ChatRole.User).Text;
        userMsg.ShouldContain("accuracy");
        userMsg.ShouldContain("brevity");
    }

    [Fact]
    public void Score_LmFailure_Wraps_LmCallException()
    {
        var chat = new FakeChatClient().EnqueueError(new TimeoutException("slow"));
        Should.Throw<LmCallException>(() =>
            new LLMAsJudgeMetric<QA>(chat, "Judge").Score(new QA(), new QA(), Meta));
    }

    [Fact]
    public void Score_NullProperty_HitsNullBranch()
    {
        // NullableSig has string? properties, defaulting to null — exercises null => "" branch in Read()
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.9, \"reasoning\": \"ok\"}");
        new LLMAsJudgeMetric<NullableSig>(chat, "Judge")
            .Score(new NullableSig(), new NullableSig(), NullableMeta).ShouldBe(0.9);
    }

    [Fact]
    public void Score_IFormattableProperty_HitsIFormattableBranch()
    {
        // NumericSig has a double output — exercises IFormattable branch in Read()
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.8, \"reasoning\": \"r\"}");
        new LLMAsJudgeMetric<NumericSig>(chat, "Judge")
            .Score(new NumericSig { Question = "q", Value = 42.5 }, new NumericSig { Question = "q", Value = 1.0 }, NumericMeta)
            .ShouldBe(0.8);
    }

    [Fact]
    public void Score_ObjectFallbackProperty_HitsFallbackBranch()
    {
        // ListSig has List<string> output — not null, not string, not IFormattable — exercises _ => v.ToString()! branch in Read()
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.6, \"reasoning\": \"fallback\"}");
        new LLMAsJudgeMetric<ListSig>(chat, "Judge")
            .Score(new ListSig { Question = "q", Tags = ["a", "b"] }, new ListSig { Question = "q", Tags = ["c"] }, ListMeta)
            .ShouldBe(0.6);
    }

    [Fact]
    public void Score_MissingReasoningKey_DefaultsToEmptyString()
    {
        // Exercises TryGetProperty("reasoning") == false branch in Score
        var sink = new InMemoryTraceSink();
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.4}");
        new LLMAsJudgeMetric<QA>(chat, "Judge", traceSink: sink)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.4);
        sink.Records[0].Metadata[TraceKeys.Reasoning].ShouldBe("");
    }

    [Fact]
    public void Score_NullReasoningValue_DefaultsToEmptyString()
    {
        // Exercises r.GetString() ?? "" null branch — JSON "reasoning": null gives JsonElement.GetString() == null
        var sink = new InMemoryTraceSink();
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.55, \"reasoning\": null}");
        new LLMAsJudgeMetric<QA>(chat, "Judge", traceSink: sink)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.55);
        sink.Records[0].Metadata[TraceKeys.Reasoning].ShouldBe("");
    }

    [Fact]
    public void Score_WithActivityListener_CoversActivityNonNullBranch()
    {
        // Installs an ActivitySource listener so activity is non-null, covering activity?.SetTag branch
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.7, \"reasoning\": \"good\"}");
        new LLMAsJudgeMetric<QA>(chat, "Judge")
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.7);
    }

    [Fact]
    public void Score_SinkWriteThrows_SwallowsException()
    {
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.3, \"reasoning\": \"x\"}");
        var sink = new ThrowingTraceSink();
        // Should not throw even though sink.Write throws
        new LLMAsJudgeMetric<QA>(chat, "Judge", traceSink: sink)
            .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
            .ShouldBe(0.3);
    }

    [Fact]
    public void Score_AmbientContext_RunsHookPipeline_AroundJudgeCall()
    {
        var chat = new FakeChatClient().EnqueueText("{\"score\": 0.9, \"reasoning\": \"ok\"}");
        var hook = new SpyHook();
        var ambient = new AmpelumContext(
            new FakeChatClient(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        using (AmpelumContext.Push(ambient))
        {
            new LLMAsJudgeMetric<QA>(chat, "Judge")
                .Score(new QA { Answer = "a" }, new QA { Answer = "b" }, Meta)
                .ShouldBe(0.9);
        }

        hook.Calls.ShouldContain("before:LLMAsJudgeMetric.Score");
        hook.Calls.ShouldContain("after:LLMAsJudgeMetric.Score");
    }

    [Fact]
    public void Score_AmbientContext_NoHooks_LmFailure_Wraps_LmCallException()
    {
        // No Hooks on the ambient context — HookedLmCall's no-hooks fast path calls the judge
        // client directly (no wrapping), so LLMAsJudgeMetric's own catch wraps it, same as the
        // no-ambient-context path.
        var chat = new FakeChatClient().EnqueueError(new TimeoutException("slow"));
        var ambient = new AmpelumContext(
            new FakeChatClient(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

        using (AmpelumContext.Push(ambient))
        {
            var ex = Should.Throw<LmCallException>(() =>
                new LLMAsJudgeMetric<QA>(chat, "Judge").Score(new QA(), new QA(), Meta));
            ex.Message.ShouldBe("LLMAsJudgeMetric judge call failed.");
        }
    }

    [Fact]
    public void Score_AmbientContext_WithHooks_LmFailure_PassesThroughLmCallException_NotDoubleWrapped()
    {
        // With Hooks set, HookedLmCall itself wraps the failure in LmCallException("IChatClient.GetResponseAsync failed.").
        // LLMAsJudgeMetric's outer catch must let that LmCallException through unwrapped rather than re-wrapping it.
        var chat = new FakeChatClient().EnqueueError(new TimeoutException("slow"));
        var hook = new SpyHook();
        var ambient = new AmpelumContext(
            new FakeChatClient(), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        using (AmpelumContext.Push(ambient))
        {
            var ex = Should.Throw<LmCallException>(() =>
                new LLMAsJudgeMetric<QA>(chat, "Judge").Score(new QA(), new QA(), Meta));
            ex.Message.ShouldBe("IChatClient.GetResponseAsync failed.");
        }

        hook.Calls.ShouldContain("error:LLMAsJudgeMetric.Score");
    }

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
            Calls.Add($"error:{ctx.ModuleName}");
            return default;
        }
    }

    sealed class ThrowingTraceSink : ITraceSink
    {
        public void Write(TraceRecord record) => throw new InvalidOperationException("sink broken");
    }
}
