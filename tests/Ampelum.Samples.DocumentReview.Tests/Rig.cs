using Ampelum.Samples.DocumentReview;

namespace Ampelum.Samples.DocumentReview.Tests;

/// <summary>One desk wired to the sample's own fake services, with a private work directory that is removed on dispose.</summary>
internal sealed class Rig : IAsyncDisposable
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "ampelum-document-review");

    // Tests run side by side; create and remove go through one lock so the shared parent is never removed under a creator.
    static readonly object Gate = new();

    Rig(ReviewSettings settings, int inProgressPolls)
    {
        WorkDir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        lock (Gate)
            Directory.CreateDirectory(WorkDir);
        Rerank = new FakeRerankHandler(FakeData.RerankerKey);
        Agent = new FakeAgentHandler();
        Batch = new FakeBatchHandler(FakeData.BatchKey, inProgressPolls);
        Clock = new InstantTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Transport = new ReviewTransport(Rerank, Agent, Batch, Clock, FakeData.CohereEndpoint, FakeData.VoyageEndpoint, FakeData.BatchBase);
        Settings = settings;
        Desk = new ReviewDesk(settings, Transport);
    }

    public string WorkDir { get; }
    public FakeRerankHandler Rerank { get; }
    public FakeAgentHandler Agent { get; }
    public FakeBatchHandler Batch { get; }
    public InstantTimeProvider Clock { get; }
    public ReviewTransport Transport { get; }
    public ReviewSettings Settings { get; }
    public ReviewDesk Desk { get; }

    public static Rig Create(int inProgressPolls = 2, ReviewSettings? settings = null) =>
        new(settings ?? FakeData.Settings(), inProgressPolls);

    /// <summary>Settings backed by a fixed set of values; any other name is missing.</summary>
    public static ReviewSettings SettingsOf(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return new ReviewSettings(name => map.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing setting {name}."));
    }

    public async Task<IReadOnlyList<LoadedFile>> LoadAsync(CancellationToken cancellationToken)
    {
        var paths = await SupplierDocuments.WriteAllAsync(WorkDir, cancellationToken);
        return await Desk.LoadAsync(paths, cancellationToken);
    }

    public async Task<IReadOnlyList<LoadedFile>> ReadableAsync(CancellationToken cancellationToken) =>
        [.. (await LoadAsync(cancellationToken)).Where(f => f.Pages.Count > 0)];

    public ValueTask DisposeAsync()
    {
        lock (Gate)
        {
            Directory.Delete(WorkDir, recursive: true);
            if (!Directory.EnumerateFileSystemEntries(Root).Any())
                Directory.Delete(Root);
        }
        return ValueTask.CompletedTask;
    }
}
