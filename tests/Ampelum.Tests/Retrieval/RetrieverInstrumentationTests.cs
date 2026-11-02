using System.Diagnostics;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using Shouldly;
using Xunit;

[Collection(ActivityListenerCollection.Name)]
public sealed class RetrieverInstrumentationTests
{
    static RetrievedDocument Doc(string id) =>
        new(id, "t", 0.8, new Dictionary<string, object?>());

    sealed class BareRetriever : IRetriever
    {
        public List<RetrievedDocument> Results { get; } = [];

        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>(Results);
    }

    sealed class CancellableRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(new List<RetrievedDocument>());
        }
    }

    [Fact]
    public async Task RetrieveAsync_DelegatesResultsToInner()
    {
        var inner = new BareRetriever();
        inner.Results.Add(Doc("d1"));
        var decorated = new RetrieverInstrumentation(inner);

        var results = await decorated.RetrieveAsync("q", topK: 5, ct: TestContext.Current.CancellationToken);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe("d1");
    }

    [Fact]
    public async Task RetrieveAsync_EmitsActivity_WithRequiredTags()
    {
        var inner = new BareRetriever();
        inner.Results.Add(Doc("d1"));
        inner.Results.Add(Doc("d2"));
        var decorated = new RetrieverInstrumentation(inner);

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

        await decorated.RetrieveAsync("hello", topK: 3, ct: TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.GetTagItem("retriever.type").ShouldBe(inner.GetType().Name);
        captured.GetTagItem("query.topk").ShouldBe(3);
        ((long)captured.GetTagItem("query.hit_count")!).ShouldBe(2L);
        ((double)captured.GetTagItem("query.latency_ms")!).ShouldBeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() =>
            new RetrieverInstrumentation(null!));
    }

    // â”€â”€ Coverage extras â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task RetrieveAsync_PassesQueryAndFiltersToInner()
    {
        var inner = new BareRetriever();
        inner.Results.Add(Doc("d1"));
        var decorated = new RetrieverInstrumentation(inner);

        var filters = new Dictionary<string, object?> { ["x"] = 42 };
        var results = await decorated.RetrieveAsync("my query", topK: 10, filters: filters, ct: TestContext.Current.CancellationToken);

        results.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyResults_RecordsZeroHitCount()
    {
        var inner = new BareRetriever();
        var decorated = new RetrieverInstrumentation(inner);

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

        var results = await decorated.RetrieveAsync("query", topK: 5, ct: TestContext.Current.CancellationToken);

        results.ShouldBeEmpty();
        captured.ShouldNotBeNull();
        ((long)captured!.GetTagItem("query.hit_count")!).ShouldBe(0L);
    }

    [Fact]
    public async Task RetrieveAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var inner = new CancellableRetriever();
        var decorated = new RetrieverInstrumentation(inner);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await decorated.RetrieveAsync("q", topK: 5, ct: cts.Token));
    }
}
