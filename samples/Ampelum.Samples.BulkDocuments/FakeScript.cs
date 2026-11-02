using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Testing;

namespace Ampelum.Samples.BulkDocuments;

/// <summary>Two scripted chat endpoints and helpers to queue their replies for fake mode and tests.</summary>
public sealed class FakeEndpoints
{
    public FakeChatClient Primary { get; } = new();

    public FakeChatClient Fallback { get; } = new();

    public IReadOnlyList<ProviderEndpoint> All => [new("primary", Primary), new("fallback", Fallback)];
}

public static class FakeScript
{
    /// <summary>Queues a reply that parses into a summary.</summary>
    public static void EnqueueSummary(FakeChatClient client, string summary) =>
        client.EnqueueOutputs(new SummarySignature { Text = "-", Summary = summary });

    /// <summary>Queues a streamed reply: the summary arrives as one chunk per field.</summary>
    public static void EnqueueStreamedSummary(FakeChatClient client, string summary) =>
        client.EnqueueStreamingOutputs(new SummarySignature { Text = "-", Summary = summary });

    /// <summary>Settings for a run without network: placeholder model, no waiting between status checks.</summary>
    public static BulkDocumentsOptions FakeOptions() => new()
    {
        BatchModel = "fake-batch-model",
        PollInterval = TimeSpan.FromMilliseconds(1),
    };
}
