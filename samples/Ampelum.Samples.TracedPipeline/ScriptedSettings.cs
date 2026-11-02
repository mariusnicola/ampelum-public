namespace Ampelum.Samples.TracedPipeline;

/// <summary>Placeholder destinations for --fake and tests. Nothing here is a working credential.</summary>
public static class ScriptedSettings
{
    /// <summary>Settings whose hosts never resolve, so a request that escapes the fake handler fails.</summary>
    public static TracingSettings Create() => new(
        LangFusePublicKey: "fake-langfuse-public",
        LangFuseSecretKey: "fake-langfuse-secret",
        LangFuseEndpoint: new Uri("http://langfuse.invalid"),
        LangSmithApiKey: "fake-langsmith-key",
        LangSmithProject: "fake-langsmith-project",
        LangSmithEndpoint: new Uri("http://langsmith.invalid"),
        PhoenixEndpoint: new Uri("http://phoenix.invalid:6006"),
        PhoenixApiKey: "fake-phoenix-key");
}
