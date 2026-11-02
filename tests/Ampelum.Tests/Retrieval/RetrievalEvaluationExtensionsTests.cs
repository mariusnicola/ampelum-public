using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Evaluation;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class RetrievalEvaluationExtensionsTests
{
    private sealed class StubMetric : IRetrievalMetric
    {
        public string Name => "stub";
        public double Compute(IReadOnlyList<string> r, IReadOnlyList<string> g, int? k = null)
            => r.Intersect(g).Any() ? 1 : 0;
    }

    private sealed class StubRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                [new RetrievedDocument(q, q, 1, new Dictionary<string, object?>())]);
    }

    [Fact]
    public async Task Evaluate_AveragesMetricAcrossDataset()
    {
        var sut = new StubRetriever();
        var data = new[]
        {
            ("a", (IReadOnlyList<string>)new[] { "a" }),
            ("b", (IReadOnlyList<string>)new[] { "x" }),
        };
        var r = await sut.EvaluateRetrievalAsync(data, [new StubMetric()], 1, TestContext.Current.CancellationToken);
        r["stub"].ShouldBe(0.5, 0.0001);
    }

    [Fact]
    public async Task Evaluate_MultipleMetrics_AllReported()
    {
        var sut = new StubRetriever();
        var data = new[]
        {
            ("a", (IReadOnlyList<string>)new[] { "a" }),
        };
        var r = await sut.EvaluateRetrievalAsync(data, [new StubMetric()], 1, TestContext.Current.CancellationToken);
        r.Keys.ShouldContain("stub");
    }
}
