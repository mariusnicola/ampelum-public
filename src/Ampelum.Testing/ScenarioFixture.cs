namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// Base fixture for Tier 3 (Scenario Smoke) tests.
/// Reads <c>AMPELUM_E2E_SCENARIOS</c> env var; if absent or <c>false</c>, tests skip.
/// Always uses disk cache. Subclass should add <c>[Collection("Scenarios")]</c>.
/// </summary>
/// <remarks>
/// Tier 3 tests are non-blocking — run manually or on nightly CI.
/// The subclass provides the <see cref="E2EFixture"/> abstract members.
/// </remarks>
public abstract class ScenarioFixture : E2EFixture
{
    /// <summary>True when the <c>AMPELUM_E2E_SCENARIOS</c> env var is set to a truthy value.</summary>
    public bool IsEnabled { get; }

    /// <summary>True when the OpenRouter API key is available.</summary>
    public bool HasApiKey { get; }

    /// <summary>Initializes the fixture and checks scenario environment.</summary>
    protected ScenarioFixture()
    {
        IsEnabled = IsScenarioEnabled();
        HasApiKey = HasApiKeyConfigured();
    }

    /// <summary>
    /// Returns a caching context for scenario tests.
    /// </summary>
    public AmpelumContext GetCachedContext() => CreateCachingContext();

    private static bool IsScenarioEnabled()
    {
        var val = Environment.GetEnvironmentVariable("AMPELUM_E2E_SCENARIOS");
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(val, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasApiKeyConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AMPELUM_INTEGRATION_OPENROUTER_KEY"));
}