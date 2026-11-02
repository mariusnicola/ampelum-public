using System.Diagnostics;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Observability;

/// <summary>
/// Verifies v0.7 OTel GenAI semconv 1.27.0 adoption (Task 4).
///
/// Production code under test:
/// <list type="bullet">
///   <item>replaces <c>SignalGrid.Ampelum.tokens.input/output</c> â†’ <c>gen_ai.usage.input_tokens/output_tokens</c></item>
///   <item>renames <c>SignalGrid.Ampelum.signature</c> â†’ <c>SignalGrid.Ampelum.signature_type</c></item>
///   <item>adds <c>gen_ai.system / request.model / response.model / response.id / operation.name / semconv.version</c></item>
/// </list>
/// Library-internal <c>SignalGrid.Ampelum.module / .adapter / .signature_type / .demos.count / .trace_id</c> stay
/// as <c>SignalGrid.Ampelum.*</c> until v0.8 rebrand.
/// </summary>
[Collection(ActivityListenerCollection.Name)]
public sealed class GenAiSemconvAttributesTests
{
    [Fact]
    public async Task PredictForward_EmitsGenAiUsageAndModelTags()
    {
        // The listener is process-wide, so only this test's trace may reach the list.
        using var parent = new Activity("usage-and-model-tags-test").Start();
        var traceId = parent.TraceId;
        var captured = new List<KeyValuePair<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.TraceId == traceId)
                    captured.AddRange(a.TagObjects);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var lm = new FakeChatClient();
        lm.EnqueueText(
            "[[ ## Answer ## ]]\n42",
            usage: new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            modelId: "test-model",
            responseId: "resp-abc");
        var ctx = new AmpelumContext(lm, new ChatAdapter(),
            SignalGrid.Ampelum.NullTraceSink.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var predict = new Predict<MathQa>(ctx);

        _ = await predict.ForwardAsync(
            new MathQa { Question = "2+2" },
            ct: TestContext.Current.CancellationToken);

        var tags = captured.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        tags["gen_ai.usage.input_tokens"].ShouldBe(10L);
        tags["gen_ai.usage.output_tokens"].ShouldBe(5L);
        tags["gen_ai.response.model"].ShouldBe("test-model");
        tags["gen_ai.response.id"].ShouldBe("resp-abc");
        tags["gen_ai.operation.name"].ShouldBe("chat");
        tags["gen_ai.semconv.version"].ShouldBe("1.27.0");
        tags.ShouldContainKey("gen_ai.system");
        tags.ShouldNotContainKey("SignalGrid.Ampelum.tokens.input");
        tags.ShouldNotContainKey("SignalGrid.Ampelum.tokens.output");
        tags.ShouldNotContainKey("SignalGrid.Ampelum.signature");
        tags["SignalGrid.Ampelum.module"].ShouldBe("Predict");
        tags["SignalGrid.Ampelum.signature_type"].ShouldBe(nameof(MathQa));
    }

    [Fact]
    public async Task PredictStream_EmitsGenAiUsageAndModelTagsOnFinalize()
    {
        // The listener is process-wide, so only this test's trace may reach the list.
        using var parent = new Activity("stream-tags-test").Start();
        var traceId = parent.TraceId;
        var captured = new List<KeyValuePair<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.TraceId == traceId)
                    captured.AddRange(a.TagObjects);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var lm = new FakeChatClient();
        lm.EnqueueStreamingChunks(
            "[[ ## Answer ## ]]\n",
            "forty-two");
        var ctx = new AmpelumContext(lm, new ChatAdapter(),
            SignalGrid.Ampelum.NullTraceSink.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var predict = new Predict<MathQa>(ctx);

        await foreach (var _ in predict.StreamAsync(
            new MathQa { Question = "2+2" },
            ct: TestContext.Current.CancellationToken))
        {
        }

        var tags = captured.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        tags.ShouldContainKey("gen_ai.system");
        tags["gen_ai.operation.name"].ShouldBe("chat");
        tags["gen_ai.semconv.version"].ShouldBe("1.27.0");
        tags.ShouldNotContainKey("SignalGrid.Ampelum.signature");
        tags["SignalGrid.Ampelum.signature_type"].ShouldBe(nameof(MathQa));
    }

    [Theory]
    [InlineData("OpenAI", "openai")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("OpenRouter", "openrouter")]
    [InlineData("UnknownProvider", "unknown")]
    [InlineData(null, "unknown")]
    public async Task PredictForward_GenAiSystem_MapsProviderName(string? providerName, string expected)
    {
        // The listener is process-wide, so only this test's trace may reach the list.
        using var parent = new Activity("provider-name-test").Start();
        var traceId = parent.TraceId;
        var captured = new List<KeyValuePair<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.TraceId == traceId)
                    captured.AddRange(a.TagObjects);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var lm = new ProviderTaggedFakeClient(providerName);
        var ctx = new AmpelumContext(lm, new ChatAdapter(),
            SignalGrid.Ampelum.NullTraceSink.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var predict = new Predict<MathQa>(ctx);

        _ = await predict.ForwardAsync(
            new MathQa { Question = "?" },
            ct: TestContext.Current.CancellationToken);

        var tags = captured.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        tags["gen_ai.system"].ShouldBe(expected);
    }

    sealed record MathQa : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string? Answer { get; init; }
    }

    /// <summary>Test client that exposes a configurable provider name via <see cref="ChatClientMetadata"/>.</summary>
    sealed class ProviderTaggedFakeClient(string? providerName) : IChatClient
    {
        readonly ChatClientMetadata _md = new("Test", null, null) { };

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[[ ## Answer ## ]]\n42")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
                return providerName is null ? null : new ChatClientMetadata(providerName);
            return null;
        }

        public void Dispose() { }
    }
}
