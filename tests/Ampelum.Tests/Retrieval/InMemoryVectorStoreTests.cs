using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

public sealed class InMemoryVectorStoreTests
{
    static RetrievedDocument Doc(string id, string text = "t") =>
        new(id, text, 0, new Dictionary<string, object?> { ["cat"] = id[0].ToString() });

    static ReadOnlyMemory<float> Vec(params float[] v) => new(v);

    [Fact]
    public async Task Query_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemoryVectorStore();
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 5, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpsertThenQuery_ReturnsUpsertedDocs()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 1, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Document.Id.ShouldBe("a");
    }

    [Fact]
    public async Task Upsert_ReplacesByIdNotDuplicate()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        await store.UpsertAsync([Doc("a")], [Vec(0f, 1f)], TestContext.Current.CancellationToken);
        var results = await store.QueryAsync(Vec(0f, 1f), topK: 5, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Document.Id.ShouldBe("a");
    }

    [Fact]
    public async Task Query_ReturnsAtMostTopK()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [Doc("a"), Doc("b"), Doc("c")],
            [Vec(1f, 0f), Vec(0f, 1f), Vec(1f, 1f)],
            TestContext.Current.CancellationToken);
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 2, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Query_WhenCorpusSmallerThanTopK_ReturnsAll()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a"), Doc("b")], [Vec(1f, 0f), Vec(0f, 1f)], TestContext.Current.CancellationToken);
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 100, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Query_ScoresNormalisedToZeroOneRange()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 1, ct: TestContext.Current.CancellationToken);
        results[0].Score.ShouldBeInRange(0.0, 1.0);
        // Identical vectors → cosine = 1 → normalised score = (1+1)/2 = 1.
        results[0].Score.ShouldBe(1.0, tolerance: 1e-9);
    }

    [Fact]
    public async Task Query_OrthogonalVectors_ScoreIsHalf()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        // [0,1] is orthogonal to [1,0]: cosine = 0 → normalised = (0+1)/2 = 0.5
        var results = await store.QueryAsync(Vec(0f, 1f), topK: 1, ct: TestContext.Current.CancellationToken);
        results[0].Score.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public async Task Query_EqualityFilter_ExcludesNonMatching()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
            [Doc("a"), Doc("b")],
            [Vec(1f, 0f), Vec(1f, 0f)],
            TestContext.Current.CancellationToken);
        var filters = new Dictionary<string, object?> { ["cat"] = "a" };
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 5, filters: filters, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Document.Id.ShouldBe("a");
    }

    [Fact]
    public async Task Upsert_MismatchedCounts_ThrowsArgumentException()
    {
        var store = new InMemoryVectorStore();
        await Should.ThrowAsync<ArgumentException>(() =>
            store.UpsertAsync([Doc("a")], Array.Empty<ReadOnlyMemory<float>>(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentQueries_AllSeeConsistentSnapshot()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);

        // Start many concurrent queries and a concurrent upsert.
        var queryTasks = Enumerable.Range(0, 20)
            .Select(_ => store.QueryAsync(Vec(1f, 0f), topK: 5, ct: TestContext.Current.CancellationToken))
            .ToArray();
        var upsertTask = store.UpsertAsync([Doc("b")], [Vec(0f, 1f)], TestContext.Current.CancellationToken);

        await Task.WhenAll(queryTasks.Append(upsertTask));

        // Each query result should be either {a} or {a,b} — never torn.
        foreach (var t in queryTasks)
        {
            var r = await t;
            r.Count.ShouldBeGreaterThanOrEqualTo(1);
            r.All(x => x.Document.Id is "a" or "b").ShouldBeTrue();
        }
    }

    // --- Coverage gap: cancelled token at start of UpsertAsync ---
    [Fact]
    public async Task UpsertAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryVectorStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], cts.Token));
    }

    // --- Coverage gap: cancelled token at start of QueryAsync ---
    [Fact]
    public async Task QueryAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryVectorStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.QueryAsync(Vec(1f, 0f), topK: 5, ct: cts.Token));
    }

    // --- Coverage gap: denom < 1e-12 zero-vector branch ---
    [Fact]
    public async Task Query_ZeroVector_ScoreIsHalf()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        // Zero query vector → denom < 1e-12 → cosine = 0 → normalised = 0.5
        var results = await store.QueryAsync(Vec(0f, 0f), topK: 1, ct: TestContext.Current.CancellationToken);
        results[0].Score.ShouldBe(0.5, tolerance: 1e-9);
    }

    // --- Coverage gap: a.Length == 0 || b.Length == 0 branch ---
    [Fact]
    public async Task Query_EmptyQueryVector_ScoreIsHalf()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        // Empty query vector → a.Length == 0 → cosine = 0 → normalised = 0.5
        var results = await store.QueryAsync(Vec(), topK: 1, ct: TestContext.Current.CancellationToken);
        results[0].Score.ShouldBe(0.5, tolerance: 1e-9);
    }

    // --- Coverage gap: filter key missing from metadata ---
    [Fact]
    public async Task Query_FilterKeyMissingFromMetadata_ExcludesDoc()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([Doc("a")], [Vec(1f, 0f)], TestContext.Current.CancellationToken);
        var filters = new Dictionary<string, object?> { ["nonexistent"] = "x" };
        var results = await store.QueryAsync(Vec(1f, 0f), topK: 5, filters: filters, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }
}
