namespace Ampelum.IntegrationTests.Skips;

/// <summary>
/// Skip predicate for tests that require a real OpenRouter API key set in the
/// <c>AMPELUM_INTEGRATION_OPENROUTER_KEY</c> environment variable. Per spec §E4
/// these tests cost money and are skipped by default in CI/dev unless the key
/// is explicitly supplied (cost-cap is the caller's responsibility).
/// </summary>
public static class SkipUnlessRealLm
{
    /// <summary>Environment variable name probed at runtime.</summary>
    public const string EnvVarName = "AMPELUM_INTEGRATION_OPENROUTER_KEY";

    /// <summary>True when the env var is missing or empty; xUnit skip predicate.</summary>
    public static bool ShouldSkip =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>Human-readable skip reason surfaced in the test report.</summary>
    public const string Reason =
        "AMPELUM_INTEGRATION_OPENROUTER_KEY not set; skipping real-LM integration test (per spec E4).";
}

/// <summary>Skip predicate for tests that only run on Linux (e.g. AOT publish probes).</summary>
public static class SkipUnlessLinux
{
    /// <summary>True when not running on Linux.</summary>
    public static bool ShouldSkip => !OperatingSystem.IsLinux();

    /// <summary>Human-readable skip reason.</summary>
    public const string Reason = "Linux-x64 only.";
}

/// <summary>Skip predicate that requires an AOT-published probe binary at the path in <c>AMPELUM_AOT_PROBE_PATH</c>.</summary>
public static class SkipUnlessAotPublished
{
    /// <summary>Environment variable name probed at runtime.</summary>
    public const string EnvVarName = "AMPELUM_AOT_PROBE_PATH";

    /// <summary>True when the env var is missing or empty.</summary>
    public static bool ShouldSkip =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVarName));

    /// <summary>Human-readable skip reason.</summary>
    public const string Reason = "AMPELUM_AOT_PROBE_PATH not set; AOT artifact missing.";
}
