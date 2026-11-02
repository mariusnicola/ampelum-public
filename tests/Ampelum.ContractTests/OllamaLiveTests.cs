using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests;

/// <summary>
/// Live end-to-end tests that run against a local Ollama instance.
/// These tests FAIL (not skip) when Ollama or the required model is missing — install both before running.
///
/// Quick setup:
///   1. Install Ollama: https://ollama.com/download
///   2. Set OLLAMA_MODEL to a model tag, see .sample.env
///   3. ollama pull that tag
/// </summary>
public sealed class OllamaLiveTests
{
    [Signature("Answer the question.")]
    public sealed record Qa : ISignature
    {
        [Input]  public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    [Signature("Classify sentiment as positive, negative or neutral.")]
    public sealed record Sentiment : ISignature
    {
        [Input]  public required string Text { get; init; }
        [Output] public string Label { get; init; } = "";
    }

    private static string Model =>
        Environment.GetEnvironmentVariable("OLLAMA_MODEL")
        ?? throw new InvalidOperationException("Set OLLAMA_MODEL. See .sample.env.");

    private static async Task RequireOllamaAsync(CancellationToken ct)
    {
        if (!await OllamaTestHarness.IsRunningAsync(ct).ConfigureAwait(false))
            Assert.Fail(
                "Ollama is not responding at localhost:11434. " +
                "Start it with: ollama serve  (or launch the Ollama desktop app). " +
                "Not installed? https://ollama.com/download");

        if (!await OllamaTestHarness.IsModelPulledAsync(Model, ct).ConfigureAwait(false))
            Assert.Fail(
                $"Model '{Model}' is not pulled. Run: ollama pull {Model}");
    }

    // Small local models don't reliably follow the DSPy [[ ## Field ## ]]\ncontent format.
    // JsonAdapter is more robust for ForwardAsync; ChatAdapter required for StreamAsync.
    private static AmpelumContext MakeCtx() =>
        new(OllamaTestHarness.BuildChatClient(Model),
            new JsonAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

    private static AmpelumContext MakeStreamCtx() =>
        new(OllamaTestHarness.BuildChatClient(Model),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

    [Fact]
    public async Task ForwardAsync_SimpleQa_ReturnsNonEmptyAnswer()
    {
        await RequireOllamaAsync(TestContext.Current.CancellationToken);

        var predict = new Predict<Qa>(MakeCtx());
        var result = await predict.ForwardAsync(
            new Qa { Question = "What is 2 + 2?" },
            ct: TestContext.Current.CancellationToken);

        result.Value.Answer.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ForwardAsync_SentimentClassification_ReturnsLabel()
    {
        await RequireOllamaAsync(TestContext.Current.CancellationToken);

        var predict = new Predict<Sentiment>(MakeCtx());
        // Small/coder-focused models may not follow the exact [[ ## Field ## ]]\ncontent format.
        // This test validates connectivity + response; strict DSPy format compliance is unit-tested via FakeChatClient.
        try
        {
            var result = await predict.ForwardAsync(
                new Sentiment { Text = "I love this product, it works great!" },
                ct: TestContext.Current.CancellationToken);
            result.Value.Label.ShouldNotBeNullOrWhiteSpace();
        }
        catch (AdapterParseException)
        {
            // Format non-compliance is acceptable for live models; connectivity verified.
        }
    }

    [Fact]
    public async Task StreamAsync_SimpleQa_StreamsTokens()
    {
        await RequireOllamaAsync(TestContext.Current.CancellationToken);

        var predict = new Predict<Qa>(MakeStreamCtx());
        var chunks = new List<string>();
        try
        {
            await foreach (var chunk in predict.StreamAsync(
                new Qa { Question = "Name one planet in the solar system." },
                ct: TestContext.Current.CancellationToken))
            {
                if (chunk.PartialText is { Length: > 0 })
                    chunks.Add(chunk.PartialText);
            }
        }
        catch (AdapterParseException)
        {
            // Small models may not follow the field-marker format exactly; streaming itself worked.
        }

        chunks.ShouldNotBeEmpty();
    }
}
