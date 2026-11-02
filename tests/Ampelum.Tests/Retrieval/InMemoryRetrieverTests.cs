using System.Diagnostics;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

public sealed class InMemoryRetrieverTests
{
    static RetrievedDocument Doc(string id, string text, string? cat = null) =>
        new(id, text, 0,
            cat is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["cat"] = cat });

    // â”€â”€ Plan tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task RetrieveAsync_RanksRelevantDocHigher()
    {
        // Tokeniser uses exact token match: "cat" matches "cat" not "cats".
        // Doc "a" has token "cat" (2 total matches with "mat" not matching), doc "b" has no matches.
        // Doc "c" contains "cat" too â€” both a and c should appear in topK:2.
        var corpus = new[]
        {
            Doc("a", "the cat sat on the mat"),
            Doc("b", "the dog ran in the park"),
            Doc("c", "cat owner great pet"),
        };
        var retriever = new InMemoryRetriever(corpus);
        var results = await retriever.RetrieveAsync("cat", topK: 2, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(2);
        // "cat" appears in a and c; b has no match.
        results.Select(r => r.Id).ShouldContain("a");
        results.Select(r => r.Id).ShouldNotContain("b");
    }

    [Fact]
    public async Task RetrieveAsync_TopK_LimitsResults()
    {
        // Each doc contains "word" as a standalone token plus a unique word.
        var corpus = Enumerable.Range(0, 10)
            .Select(i => Doc(i.ToString(), $"word item{i}"))
            .ToArray();
        var retriever = new InMemoryRetriever(corpus);
        var results = await retriever.RetrieveAsync("word", topK: 3, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RetrieveAsync_EqualityFilter_ExcludesNonMatching()
    {
        var corpus = new[]
        {
            Doc("a", "cats", cat: "feline"),
            Doc("b", "cats", cat: "other"),
        };
        var retriever = new InMemoryRetriever(corpus);
        var filters = new Dictionary<string, object?> { ["cat"] = "feline" };
        var results = await retriever.RetrieveAsync("cats", topK: 5, filters: filters, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("a");
    }

    [Fact]
    public async Task RetrieveAsync_EmitsActivity_WithRequiredTags()
    {
        var corpus = new[] { Doc("a", "hello world") };
        var retriever = new InMemoryRetriever(corpus);

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

        await retriever.RetrieveAsync("hello", topK: 1, ct: TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.GetTagItem("retriever.type").ShouldBe(nameof(InMemoryRetriever));
        captured.GetTagItem("query.topk").ShouldBe(1);
        ((long)captured.GetTagItem("query.hit_count")!).ShouldBe(1L);
        ((double)captured.GetTagItem("query.latency_ms")!).ShouldBeGreaterThanOrEqualTo(0.0);
    }

    // â”€â”€ Coverage extras â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task RetrieveAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var corpus = new[] { Doc("a", "hello world") };
        var retriever = new InMemoryRetriever(corpus);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await retriever.RetrieveAsync("hello", topK: 1, ct: cts.Token));
    }

    [Fact]
    public void Constructor_NullCorpus_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new InMemoryRetriever(null!));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyCorpus_ReturnsEmpty()
    {
        var retriever = new InMemoryRetriever(Array.Empty<RetrievedDocument>());
        var results = await retriever.RetrieveAsync("anything", topK: 5, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_NoQueryMatches_ReturnsEmpty()
    {
        var corpus = new[]
        {
            Doc("a", "the quick brown fox"),
            Doc("b", "jumps over the lazy dog"),
        };
        var retriever = new InMemoryRetriever(corpus);
        var results = await retriever.RetrieveAsync("zzzyyyxxx", topK: 5, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_TopKExceedsMatchCount_ReturnsAll()
    {
        var corpus = new[]
        {
            Doc("a", "hello world"),
            Doc("b", "hello there"),
        };
        var retriever = new InMemoryRetriever(corpus);
        // topK=100 >> 2 matching docs â€” hits scored.Count <= topK branch
        var results = await retriever.RetrieveAsync("hello", topK: 100, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RetrieveAsync_FilterKeyAbsentFromMetadata_ExcludesDoc()
    {
        // Doc has no "cat" key at all â€” hits the !TryGetValue branch in MatchesFilters
        var corpus = new[]
        {
            Doc("a", "cats"), // no metadata key "cat"
        };
        var retriever = new InMemoryRetriever(corpus);
        var filters = new Dictionary<string, object?> { ["cat"] = "feline" };
        var results = await retriever.RetrieveAsync("cats", topK: 5, filters: filters, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_FilterValueMismatch_ExcludesDoc()
    {
        // Doc has "cat" key but wrong value â€” hits the !Equals branch in MatchesFilters
        var corpus = new[]
        {
            Doc("a", "cats", cat: "canine"),
        };
        var retriever = new InMemoryRetriever(corpus);
        var filters = new Dictionary<string, object?> { ["cat"] = "feline" };
        var results = await retriever.RetrieveAsync("cats", topK: 5, filters: filters, ct: TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_NoFilters_DoesNotSkipAnyDoc()
    {
        // Exercises the filters is null branch (no filter check)
        var corpus = new[]
        {
            Doc("a", "hello cat"),
            Doc("b", "hello dog"),
        };
        var retriever = new InMemoryRetriever(corpus);
        var results = await retriever.RetrieveAsync("hello", topK: 10, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(2);
    }
}
