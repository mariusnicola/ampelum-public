using Ampelum.Samples.BulkDocuments;
using Ampelum.Samples.Shared;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Batch;
using SignalGrid.Ampelum.Routing;
using SignalGrid.Ampelum.Testing;

var fake = args.Contains("--fake");
var cacheDirectory = Path.Combine(Path.GetTempPath(), $"ampelum-bulk-cache-{Guid.NewGuid():N}");
var documents = BulkDocumentsData.Documents;

IBatchChatClient batch;
IReadOnlyList<ProviderEndpoint> endpoints;
BulkDocumentsOptions options;
FakeEndpoints? fakeEndpoints = null;
FakeAnthropicBatchHandler? fakeBatch = null;

if (fake)
{
    // doc-2 fails in its first submission so the retry of only that document shows.
    fakeBatch = new FakeAnthropicBatchHandler().FailDocument("doc-2");
    options = FakeScript.FakeOptions();
    batch = new AnthropicBatchSubmitter(new HttpClient(fakeBatch), "fake-key");
    fakeEndpoints = new FakeEndpoints();
    // The primary is down for the first request, so the router falls over to the fallback.
    fakeEndpoints.Primary.EnqueueError(new HttpRequestException("primary down"));
    FakeScript.EnqueueSummary(fakeEndpoints.Fallback, "Revenue grew and support costs fell.");
    FakeScript.EnqueueStreamedSummary(fakeEndpoints.Primary, "A cache outage slowed checkout; a larger pool fixed it.");
    endpoints = fakeEndpoints.All;
}
else
{
    var config = SampleConfig.Load();
    options = new BulkDocumentsOptions { BatchModel = config.Require("ANTHROPIC_MODEL") };
    batch = new AnthropicBatchSubmitter(new HttpClient(), config.Require("ANTHROPIC_API_KEY"));
    var primary = config.CreateChatClient();
    // Same OpenRouter key, second model: the fallback differs by model, not by account.
    var fallback = new ChatClientBuilder(config.CreateChatClient())
        .ConfigureOptions(o => o.ModelId = config.Require("OPENROUTER_MODEL_2"))
        .Build();
    endpoints = [new ProviderEndpoint("primary", primary), new ProviderEndpoint("fallback", fallback)];
}

try
{
    using var pipeline = new BulkDocumentsPipeline(batch, endpoints, new FileDistributedCache(cacheDirectory), options);

    var summaries = await pipeline.SummarizeBatchAsync(documents);
    foreach (var summary in summaries)
        Console.WriteLine($"Batch:   {summary.DocumentId} -> {summary.Summary ?? "ERROR " + summary.Error}");
    if (fakeBatch is not null)
        Console.WriteLine($"Batches: {fakeBatch.Submissions.Count} submitted, second one carried {fakeBatch.Submissions[1]["requests"]!.AsArray().Count} request(s)");

    var first = await pipeline.SummarizeAsync(documents[0]);
    var again = await pipeline.SummarizeAsync(documents[0]);
    Console.WriteLine($"Ask:     {first}");
    Console.WriteLine($"Again:   {again} (same text: {first == again})");

    var streamed = "";
    await foreach (var update in pipeline.StreamSummaryAsync(documents[1]))
        if (update.IsFinal)
            streamed = update.Value!.Summary;
    Console.WriteLine($"Stream:  {streamed}");

    if (fakeEndpoints is not null)
    {
        Console.WriteLine($"Calls:   primary {fakeEndpoints.Primary.RecordedCalls.Count}, fallback {fakeEndpoints.Fallback.RecordedCalls.Count}");
        var ok = summaries.All(s => s.Summary is not null)
            && first == again
            && fakeEndpoints.Fallback.RecordedCalls.Count == 1
            && fakeEndpoints.Primary.RecordedCalls.Count == 2;
        return ok ? 0 : 1;
    }
    return summaries.All(s => s.Summary is not null) ? 0 : 1;
}
finally
{
    // The demo cache only needs to live for one run.
    if (Directory.Exists(cacheDirectory))
        Directory.Delete(cacheDirectory, recursive: true);
}
