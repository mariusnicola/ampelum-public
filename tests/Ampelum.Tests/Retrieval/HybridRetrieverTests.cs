using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class HybridRetrieverTests
{
    private sealed class StubRetriever(params (string id, double score)[] docs) : IRetriever
    {
        public IReadOnlyDictionary<string, object?>? CapturedFilters { get; private set; }

        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
        {
            CapturedFilters = f;
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                docs.Take(topK).Select(d => new RetrievedDocument(d.id, d.id, d.score, new Dictionary<string, object?>())).ToList());
        }
    }

    private sealed class FakeReranker(List<int> capturedSizes) : IReranker
    {
        public Task<IReadOnlyList<RetrievedDocument>> RerankAsync(string q, IReadOnlyList<RetrievedDocument> c, int k, CancellationToken ct = default)
        {
            capturedSizes.Add(c.Count);
            return Task.FromResult(c);
        }
    }

    [Fact]
    public async Task Hybrid_FusesTwoRetrievers_WithRRF()
    {
        var dense = new StubRetriever(("a", 0.9), ("b", 0.7), ("c", 0.5));
        var bm25 = new StubRetriever(("b", 5.0), ("a", 4.0), ("d", 3.0));
        var sut = new HybridRetriever(
            new IRetriever[] { dense, bm25 },
            new ReciprocalRankFusionReranker(),
            new HybridRetrieverOptions { TopKPerSource = 3 });
        var r = await sut.RetrieveAsync("q", 4, ct: TestContext.Current.CancellationToken);
        r.Take(2).Select(d => d.Id).ShouldAllBe(id => id == "a" || id == "b");
    }

    [Fact]
    public async Task Hybrid_PassesFiltersToInnerRetrievers()
    {
        var inner = new StubRetriever(("a", 1));
        var sut = new HybridRetriever(new IRetriever[] { inner }, new ReciprocalRankFusionReranker(), new HybridRetrieverOptions());
        var filters = new Dictionary<string, object?> { ["k"] = "v" };
        var r = await sut.RetrieveAsync("q", 1, filters: filters, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
        inner.CapturedFilters.ShouldBe(filters);
    }

    [Fact]
    public async Task Hybrid_NonRrfReranker_DedupesAndCallsReranker()
    {
        var inner = new StubRetriever(("a", 1), ("b", 2));
        var captured = new List<int>();
        var fakeReranker = new FakeReranker(captured);
        var sut = new HybridRetriever(new IRetriever[] { inner }, fakeReranker, new HybridRetrieverOptions());
        await sut.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        captured.ShouldBe([2]);
    }

    [Fact]
    public async Task Hybrid_NonRrf_DedupesAcrossSources()
    {
        var s1 = new StubRetriever(("a", 1), ("b", 2));
        var s2 = new StubRetriever(("b", 5), ("c", 3));
        var captured = new List<int>();
        var sut = new HybridRetriever(new IRetriever[] { s1, s2 }, new FakeReranker(captured), new HybridRetrieverOptions());
        await sut.RetrieveAsync("q", 5, ct: TestContext.Current.CancellationToken);
        captured.ShouldBe([3]); // a, b, c (b deduped)
    }
}
