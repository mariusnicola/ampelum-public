using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class ReciprocalRankFusionRerankerTests
{
    private static RetrievedDocument D(string id, double score = 0)
        => new(id, id, score, new Dictionary<string, object?>());

    [Fact]
    public void Fuse_DocAppearingInBothLists_RanksAboveSingleList()
    {
        var l1 = new RetrievedDocument[] { D("a"), D("b"), D("c") };
        var l2 = new RetrievedDocument[] { D("b"), D("a"), D("d") };
        var fused = ReciprocalRankFusionReranker.Fuse(new IReadOnlyList<RetrievedDocument>[] { l1, l2 }, k: 60, topK: 4);
        new[] { fused[0].Id, fused[1].Id }.ShouldBe(new[] { "a", "b" }, ignoreOrder: true);
    }

    [Fact]
    public void Fuse_RespectsTopK()
    {
        var l1 = (IReadOnlyList<RetrievedDocument>)Enumerable.Range(0, 10).Select(i => D($"d{i}")).ToList();
        var fused = ReciprocalRankFusionReranker.Fuse(new[] { l1 }, k: 60, topK: 3);
        fused.Count.ShouldBe(3);
    }

    [Fact]
    public void Fuse_EmptyLists_ReturnsEmpty()
    {
        var fused = ReciprocalRankFusionReranker.Fuse(new IReadOnlyList<RetrievedDocument>[] { Array.Empty<RetrievedDocument>() }, 60, 5);
        fused.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rerank_FusesSingleListWithItself()
    {
        var sut = new ReciprocalRankFusionReranker(k: 60);
        var docs = new RetrievedDocument[] { D("a", 0.9), D("b", 0.5) };
        var r = await sut.RerankAsync("q", docs, 2, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(2);
        r[0].Id.ShouldBe("a");
    }

    [Fact]
    public async Task Rerank_RespectsTopK()
    {
        var sut = new ReciprocalRankFusionReranker();
        var docs = new RetrievedDocument[] { D("a"), D("b"), D("c") };
        var r = await sut.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }
}
