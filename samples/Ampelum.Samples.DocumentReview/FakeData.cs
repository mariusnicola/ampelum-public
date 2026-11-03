namespace Ampelum.Samples.DocumentReview;

/// <summary>Stand-in settings and addresses for the offline run. Nothing here belongs to a real service.</summary>
internal static class FakeData
{
    public const string RerankerKey = "fake-reranker-key";
    public const string RerankerModel = "fake-rerank-model";
    public const string BatchKey = "fake-batch-key";
    public const string BatchModel = "fake-batch-model";

    // Loopback addresses: the sample-owned handler answers them and no socket is opened.
    public static readonly Uri AgentBase = new("http://127.0.0.1:8090/");
    public static readonly Uri AgentRpc = new("http://127.0.0.1:8090/rpc");
    public static readonly Uri CohereEndpoint = new("http://127.0.0.1:8091/cohere/rerank");
    public static readonly Uri VoyageEndpoint = new("http://127.0.0.1:8091/voyage/rerank");
    public static readonly Uri BatchBase = new("http://127.0.0.1:8092/v1/");

    public const string AgentSkill = "clause-review";

    /// <summary>Settings the offline run reads in place of the real ones.</summary>
    public static ReviewSettings Settings() => new(name => name switch
    {
        ReviewSettings.RerankerName => "cohere",
        ReviewSettings.RerankerKeyName => RerankerKey,
        ReviewSettings.RerankerModelName => RerankerModel,
        ReviewSettings.AgentUrlName => AgentBase.ToString(),
        ReviewSettings.BatchKeyName => BatchKey,
        ReviewSettings.BatchModelName => BatchModel,
        _ => throw new InvalidOperationException($"Missing setting {name}."),
    });
}
