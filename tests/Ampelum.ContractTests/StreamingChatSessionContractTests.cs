#pragma warning disable AMPELUM_V09_PREVIEW

using System.ClientModel;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Shouldly;
using Xunit;

namespace Ampelum.ContractTests;

[Trait("Category", "Contract")]
[Trait("Cost", "Cheap")]
public sealed class StreamingChatSessionContractTests
{
    [Fact(Skip = "Requires OPENROUTER_API_KEY environment variable")]
    public async Task SingleToolCallRoundTrip_ReturnsUsage()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            return;

        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL_1")
            ?? throw new InvalidOperationException("Set OPENROUTER_MODEL_1. See .sample.env.");

        // Build AmpelumContext
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1")
        };
        var chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(model)
            .AsIChatClient();

        var ctx = new AmpelumContext(
            chatClient,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        // Echo tool
        var echo = AIFunctionFactory.Create(
            (string text) => $"ECHO: {text}",
            "echo",
            "Echo the input text");

        var session = new StreamingChatSession(
            ctx,
            "You are a test assistant.",
            tools: new[] { echo });

        var tokens = new List<string>();
        await foreach (var t in session.SendAsync(
            "Call the echo tool with the text 'hello world'",
            TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        session.LastUsage.ShouldNotBeNull();
        session.LastUsage!.InputTokens.ShouldBeGreaterThan(0);
        session.LastUsage.OutputTokens.ShouldBeGreaterThan(0);
        tokens.ShouldNotBeEmpty();
    }

    [Fact(Skip = "Requires OPENROUTER_API_KEY environment variable")]
    public async Task MultiTurnSession_AccumulatesUsage()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            return;

        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL_1")
            ?? throw new InvalidOperationException("Set OPENROUTER_MODEL_1. See .sample.env.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1")
        };
        var chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(model)
            .AsIChatClient();

        var ctx = new AmpelumContext(
            chatClient,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        var session = new StreamingChatSession(
            ctx,
            "You are a helpful test assistant.");

        // First turn
        await foreach (var _ in session.SendAsync(
            "What is the capital of France?",
            TestContext.Current.CancellationToken))
        {
            // Consume tokens
        }

        session.LastUsage.ShouldNotBeNull();
        var firstTurnTotal = session.SessionTotal;

        // Second turn
        await foreach (var _ in session.SendAsync(
            "What is its population?",
            TestContext.Current.CancellationToken))
        {
            // Consume tokens
        }

        session.LastUsage.ShouldNotBeNull();
        session.SessionTotal.InputTokens.ShouldBeGreaterThanOrEqualTo(firstTurnTotal.InputTokens);
        session.SessionTotal.OutputTokens.ShouldBeGreaterThanOrEqualTo(firstTurnTotal.OutputTokens);
    }

    [Fact(Skip = "Requires OPENROUTER_API_KEY environment variable")]
    public async Task SessionWithPriorHistory_ResumesConversation()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? string.Empty;
        if (string.IsNullOrEmpty(apiKey))
            return;

        var model = Environment.GetEnvironmentVariable("OPENROUTER_MODEL_1")
            ?? throw new InvalidOperationException("Set OPENROUTER_MODEL_1. See .sample.env.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openrouter.ai/api/v1")
        };
        var chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(model)
            .AsIChatClient();

        var ctx = new AmpelumContext(
            chatClient,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);

        var priorHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "What is 2+2?"),
            new(ChatRole.Assistant, "The answer is 4.")
        };

        var session = new StreamingChatSession(
            ctx,
            "You are a helpful assistant.",
            tools: null,
            priorHistory: priorHistory);

        // History should include system + prior messages
        session.History.Count.ShouldBe(3);
        session.History[0].Role.ShouldBe(ChatRole.System);
        session.History[1].Role.ShouldBe(ChatRole.User);
        session.History[2].Role.ShouldBe(ChatRole.Assistant);

        // Continue the conversation
        var tokens = new List<string>();
        await foreach (var t in session.SendAsync(
            "What about 3+3?",
            TestContext.Current.CancellationToken))
        {
            tokens.Add(t);
        }

        tokens.ShouldNotBeEmpty();
        session.LastUsage.ShouldNotBeNull();
    }
}
