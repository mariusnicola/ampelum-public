using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// Centralized model configuration sourced from <c>.env</c> or environment variables.
/// Provides constants for use in tests, benchmarks, and production startup.
/// </summary>
/// <remarks>
/// <para><b>Canonical source</b> — the <c>.env</c> file at the repository root:</para>
/// <code>
/// OPENROUTER_API_KEY=sk-or-v1-...
/// OPENROUTER_MODEL_1=
/// OPENROUTER_MODEL_2=
/// LMSTUDIO_URL=http://localhost:1234/api/v1/chat
/// LMSTUDIO_MODEL=
/// OLLAMA_URL=http://localhost:11434
/// OLLAMA_MODEL=
/// </code>
/// Model identifiers have no code defaults: an unset variable throws
/// <see cref="InvalidOperationException"/> naming the variable and <c>.sample.env</c>.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class ModelConfig
{
    // ──────────────────────────────────────────
    // Provider identifiers
    // ──────────────────────────────────────────

    /// <summary>Provider identifier for OpenRouter.</summary>
    public const string ProviderOpenRouter = "openrouter";

    /// <summary>Provider identifier for LM Studio (local).</summary>
    public const string ProviderLmStudio = "lmstudio";

    /// <summary>Provider identifier for Ollama (local).</summary>
    public const string ProviderOllama = "ollama";

    // ──────────────────────────────────────────
    // Environment variable keys (matching .env)
    // ──────────────────────────────────────────
    private const string EnvOpenRouterKey = "OPENROUTER_API_KEY";
    private const string EnvOpenRouterModel1 = "OPENROUTER_MODEL_1";
    private const string EnvOpenRouterModel2 = "OPENROUTER_MODEL_2";
    private const string EnvLmStudioUrl = "LMSTUDIO_URL";
    private const string EnvLmStudioModel = "LMSTUDIO_MODEL";
    private const string EnvOllamaUrl = "OLLAMA_URLin";       // .env uses OLLAMA_URLin
    private const string EnvOllamaModel = "OLLAMA_MODEL";

    // ──────────────────────────────────────────
    // Resolved values (env overrides defaults)
    // ──────────────────────────────────────────

    /// <summary>Default model for OpenRouter when none is specified per-request.</summary>
    /// <remarks>Reads <c>OPENROUTER_MODEL_1</c>; throws when unset.</remarks>
    public static string DefaultModel =>
        Env(EnvOpenRouterModel1) ?? throw Missing(EnvOpenRouterModel1);

    /// <summary>Safe test model — always available, cheap, deterministic enough for unit tests.</summary>
    /// <remarks>This is the model that should be used in all test fixtures and FakeChatClient setups.</remarks>
    public static string TestModel =>
        Env(EnvOpenRouterModel2) ?? Env(EnvOpenRouterModel1) ?? throw Missing(EnvOpenRouterModel2 + " or " + EnvOpenRouterModel1);

    /// <summary>OpenRouter API key (may be empty in CI / offline tests).</summary>
    public static string? OpenRouterApiKey =>
        Env(EnvOpenRouterKey);

    /// <summary>LM Studio local endpoint.</summary>
    public static string? LmStudioUrl =>
        Env(EnvLmStudioUrl);

    /// <summary>LM Studio model identifier.</summary>
    public static string LmStudioModel =>
        Env(EnvLmStudioModel) ?? throw Missing(EnvLmStudioModel);

    /// <summary>Ollama local endpoint.</summary>
    public static string? OllamaUrl =>
        Env(EnvOllamaUrl);

    /// <summary>Ollama model identifier.</summary>
    public static string OllamaModel =>
        Env(EnvOllamaModel) ?? throw Missing(EnvOllamaModel);

    /// <summary>
    /// Resolves a model ID for the given provider.
    /// Throws <see cref="InvalidOperationException"/> for unknown providers.
    /// </summary>
    public static string GetModel(string provider) => provider.ToLowerInvariant() switch
    {
        ProviderOpenRouter => DefaultModel,
        ProviderLmStudio => LmStudioModel,
        ProviderOllama => OllamaModel,
        _ => throw new InvalidOperationException($"Unknown provider '{provider}'. Valid: openrouter, lmstudio, ollama.")
    };

    // ──────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────

    private static InvalidOperationException Missing(string variable) =>
        new($"{variable} is not set. Set it in the environment or .env; see .sample.env.");

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key).NullIfEmpty();

    private static string? NullIfEmpty(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}