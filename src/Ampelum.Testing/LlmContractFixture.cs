using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// Base fixture for Tier 2 (LLM Contract) tests.
/// Always uses disk cache backend. Exposes <c>AssertPredictAsync</c> helper.
/// The subclass provides the <see cref="IChatClient"/> and abstract members from <see cref="E2EFixture"/>.
/// </summary>
/// <remarks>
/// Subclass per use case. Each test calls <see cref="AssertPredictAsync"/> with
/// a real LLM prompt, asserting structural invariants on the response.
/// Disk cache key = full prompt; first run costs one API call, subsequent runs are instant.
/// If <c>AMPELUM_INTEGRATION_OPENROUTER_KEY</c> is not set, tests should use <c>Skip.If</c> or
/// subclass can check <c>HasApiKeyValue</c>.
/// </remarks>
public abstract class LlmContractFixture : E2EFixture
{
    /// <summary>True when the OpenRouter API key is available.</summary>
    public bool HasApiKeyValue { get; }

    /// <summary>Initializes the fixture and checks for API key availability.</summary>
    protected LlmContractFixture()
    {
        HasApiKeyValue = HasApiKey();
    }

    /// <summary>
    /// Returns a caching context. Always uses disk cache backend so contract tests
    /// survive across <c>dotnet test</c> sessions.
    /// </summary>
    public AmpelumContext GetCachedContext() => CreateCachingContext();

    /// <summary>
    /// Asserts that a real LLM call produces a non-null, non-empty response for the given
    /// prompt. Override in per-usecase fixtures to add type-specific assertions.
    /// </summary>
    protected async Task<string> AssertPredictAsync(string systemPrompt, string userMessage)
    {
        var _ = GetCachedContext(); // ensure caching is wired
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };
        var response = await Chat.GetResponseAsync(messages);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("LLM returned empty response.");
        return text;
    }

    private static bool HasApiKey() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_OPENROUTER_KEY"));
}