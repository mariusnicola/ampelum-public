using Ampelum.Samples.Shared;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>Endpoints and credentials for the three trace destinations.</summary>
/// <param name="LangFusePublicKey">LangFuse public key, the Basic-auth user name.</param>
/// <param name="LangFuseSecretKey">LangFuse secret key, the Basic-auth password.</param>
/// <param name="LangFuseEndpoint">LangFuse base address.</param>
/// <param name="LangSmithApiKey">LangSmith API key.</param>
/// <param name="LangSmithProject">LangSmith project that receives the runs.</param>
/// <param name="LangSmithEndpoint">LangSmith base address.</param>
/// <param name="PhoenixEndpoint">Phoenix collector base address; the OTLP path is appended.</param>
/// <param name="PhoenixApiKey">Phoenix API key, or null for a collector without auth.</param>
public sealed record TracingSettings(
    string LangFusePublicKey,
    string LangFuseSecretKey,
    Uri LangFuseEndpoint,
    string LangSmithApiKey,
    string LangSmithProject,
    Uri LangSmithEndpoint,
    Uri PhoenixEndpoint,
    string? PhoenixApiKey)
{
    /// <summary>Reads every destination setting from <paramref name="config"/>.</summary>
    /// <exception cref="InvalidOperationException">A required setting is missing.</exception>
    public static TracingSettings Load(SampleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new TracingSettings(
            config.Require("LANGFUSE_PUBLIC_KEY"),
            config.Require("LANGFUSE_SECRET_KEY"),
            new Uri(config.Require("LANGFUSE_ENDPOINT")),
            config.Require("LANGSMITH_API_KEY"),
            config.Require("LANGSMITH_PROJECT"),
            new Uri(config.Require("LANGSMITH_ENDPOINT")),
            new Uri(config.Require("PHOENIX_ENDPOINT")),
            OptionalKey(config, "PHOENIX_API_KEY"));
    }

    // A local Phoenix collector has no key, and SampleConfig has no optional read.
    static string? OptionalKey(SampleConfig config, string key)
    {
        try
        {
            return config.Require(key);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
