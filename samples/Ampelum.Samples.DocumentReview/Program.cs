using Ampelum.Samples.DocumentReview;
using Ampelum.Samples.Shared;

var fake = args.Contains("--fake");
var tempRoot = Path.Combine(Path.GetTempPath(), "ampelum-document-review");
var workDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
try
{
    ReviewSettings settings;
    ReviewTransport transport;
    Func<IReadOnlyList<(RerankerKind Kind, string Key, string Model)>> rerankers;
    if (fake)
    {
        settings = FakeData.Settings();
        transport = new ReviewTransport(
            new FakeRerankHandler(FakeData.RerankerKey),
            new FakeAgentHandler(),
            new FakeBatchHandler(FakeData.BatchKey, inProgressPolls: 2),
            new InstantTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            FakeData.CohereEndpoint,
            FakeData.VoyageEndpoint,
            FakeData.BatchBase);
        // The offline run shows both rerankers; a live run uses the one the setting names.
        rerankers = () => [(RerankerKind.Cohere, settings.RerankerKey(), settings.RerankerModel()), (RerankerKind.Voyage, settings.RerankerKey(), settings.RerankerModel())];
    }
    else
    {
        settings = ReviewSettings.From(SampleConfig.Load());
        transport = ReviewTransport.Live();
        rerankers = () => [settings.Reranker()];
    }

    return await ReviewRun.RunReportingErrorsAsync(settings, transport, rerankers, workDir, Console.Out, Console.Error, CancellationToken.None);
}
finally
{
    // Only this run's own folder: another run may be using the shared parent.
    Directory.Delete(workDir, recursive: true);
}
