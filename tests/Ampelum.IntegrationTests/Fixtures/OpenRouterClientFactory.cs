using System.ClientModel;
using Ampelum.IntegrationTests.Skips;
using Microsoft.Extensions.AI;
using OpenAI;
using SignalGrid.Ampelum;

namespace Ampelum.IntegrationTests.Fixtures;

/// <summary>
/// Builds a real <see cref="IChatClient"/> backed by OpenRouter using the
/// <c>AMPELUM_INTEGRATION_OPENROUTER_KEY</c> environment variable. Pareto real-LM
/// integration tests (#1â€“#5, #11, #14, #16) consume this helper so the wiring
/// lives in exactly one place. Cost-cap is the caller's responsibility.
/// </summary>
internal static class OpenRouterClientFactory
{
    /// <summary>OpenRouter HTTPS endpoint (OpenAI-compatible API surface).</summary>
    public const string Endpoint = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Default cost-conscious model used across the Pareto integration tests;
    /// resolves from <see cref="ModelConfig.DefaultModel"/> at runtime.
    /// </summary>
    public static string DefaultModel => ModelConfig.DefaultModel;

    /// <summary>
    /// Constructs an <see cref="IChatClient"/> against the OpenRouter endpoint.
    /// Throws <see cref="InvalidOperationException"/> if the env var is missing â€” call sites
    /// MUST gate with <see cref="SkipUnlessRealLm"/> first.
    /// </summary>
    public static IChatClient Create(string? model = null)
    {
        var key = Environment.GetEnvironmentVariable(SkipUnlessRealLm.EnvVarName)
            ?? throw new InvalidOperationException(
                $"{SkipUnlessRealLm.EnvVarName} not set; gate the test with SkipUnlessRealLm.");
        var options = new OpenAIClientOptions { Endpoint = new Uri(Endpoint) };
        return new OpenAIClient(new ApiKeyCredential(key), options)
            .GetChatClient(model ?? DefaultModel)
            .AsIChatClient();
    }
}
