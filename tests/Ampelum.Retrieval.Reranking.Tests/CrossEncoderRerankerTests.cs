using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Reranking;
using Shouldly;
using Xunit;

namespace Ampelum.Retrieval.Reranking.Tests;

public sealed class CrossEncoderRerankerTests
{
    private sealed class StubScorer(Func<string, string, float> fn) : IRelevanceScorer
    {
        public bool Disposed { get; private set; }
        public float Score(string query, string document) => fn(query, document);
        public void Dispose() => Disposed = true;
    }

    private static IReadOnlyList<RetrievedDocument> Docs(params string[] texts)
        => texts.Select((t, i) => new RetrievedDocument($"d{i}", t, 0, new Dictionary<string, object?>())).ToList();

    [Fact]
    public async Task Rerank_OrdersByScorerScore()
    {
        // longer text => higher score
        using var sut = new CrossEncoderReranker(new StubScorer((_, d) => d.Length));
        var docs = Docs("x", "xxxxx", "xxx");
        var r = await sut.RerankAsync("q", docs, 3, TestContext.Current.CancellationToken);
        r.Select(d => d.Text).ShouldBe(["xxxxx", "xxx", "x"]);
    }

    [Fact]
    public async Task Rerank_TopK_Truncates()
    {
        using var sut = new CrossEncoderReranker(new StubScorer((_, d) => d.Length));
        var docs = Docs("a", "bb", "ccc");
        var r = await sut.RerankAsync("q", docs, 2, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(2);
        r[0].Text.ShouldBe("ccc");
    }

    [Fact]
    public async Task Rerank_EmptyCandidates_ReturnsEmpty()
    {
        var calls = 0;
        using var sut = new CrossEncoderReranker(new StubScorer((_, _) => { calls++; return 0; }));
        var r = await sut.RerankAsync("q", Array.Empty<RetrievedDocument>(), 5, TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
        calls.ShouldBe(0);
    }

    [Fact]
    public void Dispose_DisposesScorer()
    {
        var s = new StubScorer((_, _) => 0);
        var sut = new CrossEncoderReranker(s);
        sut.Dispose();
        s.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Options_DefaultsApplied()
    {
        var o = new CrossEncoderOptions();
        o.MaxSequenceLength.ShouldBe(512);
        o.ExpectedSha256.ShouldNotBeNullOrEmpty();
        o.ModelPath.ShouldContain("Ampelum");
    }

    [Fact]
    public async Task Rerank_WithScores_OverwritesScore()
    {
        using var sut = new CrossEncoderReranker(new StubScorer((_, _) => 7.5f));
        var docs = Docs("a");
        var r = await sut.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken);
        r[0].Score.ShouldBe(7.5, 0.0001);
    }
}
