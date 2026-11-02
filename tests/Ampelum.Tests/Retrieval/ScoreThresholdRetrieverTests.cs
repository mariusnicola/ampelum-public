using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Filtering;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class ScoreThresholdRetrieverTests
{
    private sealed class StubRetriever(params (string id, double score)[] docs) : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                docs.Take(topK).Select(d => new RetrievedDocument(d.id, d.id, d.score, new Dictionary<string, object?>())).ToList());
    }

    [Fact]
    public async Task FiltersBelowThreshold()
    {
        var sut = new ScoreThresholdRetriever(new StubRetriever(("a", 0.9), ("b", 0.4), ("c", 0.7)), 0.5);
        var r = await sut.RetrieveAsync("q", 10, ct: TestContext.Current.CancellationToken);
        r.Select(d => d.Id).ShouldBe(["a", "c"]);
    }

    [Fact]
    public async Task EmptyWhenAllBelow()
    {
        var sut = new ScoreThresholdRetriever(new StubRetriever(("a", 0.1)), 0.5);
        var r = await sut.RetrieveAsync("q", 10, ct: TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
    }

    [Fact]
    public async Task PassesThroughFilters()
    {
        var captured = new Dictionary<string, object?> { ["k"] = "v" };
        var sut = new ScoreThresholdRetriever(new StubRetriever(("a", 0.9)), 0.5);
        var r = await sut.RetrieveAsync("q", 10, captured, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EqualToThreshold_IsIncluded()
    {
        var sut = new ScoreThresholdRetriever(new StubRetriever(("a", 0.5)), 0.5);
        var r = await sut.RetrieveAsync("q", 10, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }
}
