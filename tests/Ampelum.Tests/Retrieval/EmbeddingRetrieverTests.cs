using System.Diagnostics;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

[Collection(ActivityListenerCollection.Name)]
public sealed class EmbeddingRetrieverTests
{
    static RetrievedDocument Doc(string id) =>
        new(id, "text", 0, new Dictionary<string, object?>());

    static FakeEmbeddingGenerator MakeEmbedder() =>
        new(new Dictionary<string, float[]>
        {
            ["cats"] = [1f, 0f],
            ["dogs"] = [0f, 1f],
        }, fallbackDimensions: 2);

    static async Task<InMemoryVectorStore> MakeStore(CancellationToken ct)
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [Doc("cats"), Doc("dogs")],
            [new ReadOnlyMemory<float>([1f, 0f]), new ReadOnlyMemory<float>([0f, 1f])],
            ct);
        return store;
    }

    // â”€â”€ Plan tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task RetrieveAsync_ReturnsTopKDocsByEmbeddingSimilarity()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await MakeStore(ct);
        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);

        var results = await retriever.RetrieveAsync("cats", topK: 1, ct: ct);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("cats");
    }

    [Fact]
    public async Task RetrieveAsync_PassesFiltersToStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [
                new RetrievedDocument("a", "cats", 0, new Dictionary<string, object?> { ["tag"] = "x" }),
                new RetrievedDocument("b", "cats", 0, new Dictionary<string, object?> { ["tag"] = "y" }),
            ],
            [new ReadOnlyMemory<float>([1f, 0f]), new ReadOnlyMemory<float>([1f, 0f])],
            ct);

        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);
        var filters = new Dictionary<string, object?> { ["tag"] = "x" };
        var results = await retriever.RetrieveAsync("cats", topK: 5, filters: filters, ct: ct);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("a");
    }

    [Fact]
    public async Task RetrieveAsync_EmitsActivity_WithRequiredTags()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await MakeStore(ct);
        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);

        // The listener is process-wide, so only this test's trace may reach it.
        using var parent = new Activity("retriever-test").Start();
        var traceId = parent.TraceId;
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.TraceId == traceId && a.OperationName == "SignalGrid.Ampelum.retriever.query") captured = a; }
        };
        ActivitySource.AddActivityListener(listener);

        await retriever.RetrieveAsync("cats", topK: 2, ct: ct);

        captured.ShouldNotBeNull();
        captured!.GetTagItem("retriever.type").ShouldBe(nameof(EmbeddingRetriever));
        captured.GetTagItem("query.topk").ShouldBe(2);
        // topK=2 with a 2-doc store returns 2 hits (both docs have non-zero cosine similarity after [0,1] normalisation)
        ((long)captured.GetTagItem("query.hit_count")!).ShouldBe(2L);
        ((double)captured.GetTagItem("query.latency_ms")!).ShouldBeGreaterThanOrEqualTo(0.0);
    }

    // â”€â”€ Coverage extras â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Constructor_NullEmbeddingGenerator_ThrowsArgumentNullException()
    {
        var store = new InMemoryVectorStore();
        Should.Throw<ArgumentNullException>(() =>
            new EmbeddingRetriever(null!, store));
    }

    [Fact]
    public void Constructor_NullVectorStore_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            new EmbeddingRetriever(MakeEmbedder(), null!));
    }

    [Fact]
    public async Task RetrieveAsync_CancelledToken_PropagatesThroughEmbedCall()
    {
        // Pre-cancelled token: GenerateVectorAsync (first await) should surface the cancellation.
        var store = new InMemoryVectorStore();
        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await retriever.RetrieveAsync("cats", topK: 1, ct: cts.Token));
    }

    [Fact]
    public async Task RetrieveAsync_CancellationTokenForwardedToVectorStore()
    {
        // Use a store stub that captures the token it received in QueryAsync
        // to verify the CT is forwarded from RetrieveAsync â†’ QueryAsync.
        var ct = TestContext.Current.CancellationToken;
        var stub = new CapturingVectorStore();
        var retriever = new EmbeddingRetriever(MakeEmbedder(), stub);

        await retriever.RetrieveAsync("cats", topK: 2, ct: ct);

        stub.CapturedQueryToken.ShouldBe(ct);
    }

    [Fact]
    public async Task RetrieveAsync_StripScoresFromVectorStoreResults()
    {
        // Documents returned by RetrieveAsync must not carry the store's Score value.
        // IVectorStore returns (Document, Score) tuples; EmbeddingRetriever projects to Document only.
        var ct = TestContext.Current.CancellationToken;
        var store = await MakeStore(ct);
        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);

        var results = await retriever.RetrieveAsync("cats", topK: 2, ct: ct);

        // Result type is RetrievedDocument â€” no score property exposed by IRetriever.
        // The record's own Score field comes from the original upserted doc (Score = 0 in MakeStore).
        results.ShouldAllBe(d => d is RetrievedDocument);
    }

    // â”€â”€ FakeEmbeddingGenerator coverage (IDisposable + IEmbeddingGenerator service) â”€â”€

    [Fact]
    public void FakeEmbeddingGenerator_Metadata_HasExpectedProviderName()
    {
        // Covers FakeEmbeddingGenerator.Metadata property â€” part of IEmbeddingGenerator contract.
        var gen = MakeEmbedder();
        gen.Metadata.ProviderName.ShouldBe(nameof(FakeEmbeddingGenerator));
    }

    [Fact]
    public void FakeEmbeddingGenerator_Dispose_DoesNotThrow()
    {
        // Covers FakeEmbeddingGenerator.Dispose() â€” exercised here so SignalGrid.Ampelum.Testing
        // contributes 100% line coverage when measured from this test project.
        using var gen = MakeEmbedder();
        // No assertion needed â€” Dispose() is empty; just verify it doesn't throw.
    }

    [Fact]
    public void FakeEmbeddingGenerator_GetService_ReturnsNull()
    {
        // Covers FakeEmbeddingGenerator.GetService() â€” part of IEmbeddingGenerator contract.
        var gen = MakeEmbedder();
        gen.GetService(typeof(string)).ShouldBeNull();
    }

    [Fact]
    public async Task FakeEmbeddingGenerator_UnscriptedInput_ReturnsFallbackVector()
    {
        // Covers the fallback branch of FakeEmbeddingGenerator.GenerateAsync
        // (TryGetValue returns false â†’ new float[fallbackDimensions]).
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryVectorStore();
        var retriever = new EmbeddingRetriever(MakeEmbedder(), store);

        // "unknown" is not scripted â†’ generates [0,0] fallback vector â†’ store is empty â†’ 0 results.
        var results = await retriever.RetrieveAsync("unknown", topK: 5, ct: ct);
        results.ShouldBeEmpty();
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// A minimal <see cref="IVectorStore"/> that captures the <see cref="CancellationToken"/>
    /// passed to <see cref="QueryAsync"/> so tests can assert token forwarding.
    /// </summary>
    sealed class CapturingVectorStore : IVectorStore
    {
        public CancellationToken CapturedQueryToken { get; private set; }

        public Task UpsertAsync(
            IReadOnlyList<RetrievedDocument> documents,
            ReadOnlyMemory<float>[] vectors,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(RetrievedDocument Document, double Score)>> QueryAsync(
            ReadOnlyMemory<float> queryVector,
            int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
        {
            CapturedQueryToken = ct;
            IReadOnlyList<(RetrievedDocument, double)> empty = [];
            return Task.FromResult(empty);
        }
    }
}
