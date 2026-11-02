using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class BM25RetrieverTests
{
    private static IReadOnlyList<RetrievedDocument> Corpus() =>
    [
        new("d1", "the quick brown fox jumps over the lazy dog", 0, new Dictionary<string, object?> { ["lang"] = "en" }),
        new("d2", "a fast auburn fox leaps above an idle hound", 0, new Dictionary<string, object?> { ["lang"] = "en" }),
        new("d3", "completely unrelated text about networking protocols", 0, new Dictionary<string, object?> { ["lang"] = "en" }),
        new("d4", "le renard rapide saute par dessus le chien paresseux", 0, new Dictionary<string, object?> { ["lang"] = "fr" }),
    ];

    [Fact]
    public async Task Retrieves_KeywordMatchedDocs_RankedByScore()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        var r = await sut.RetrieveAsync("quick fox", 3, ct: TestContext.Current.CancellationToken);
        r[0].Id.ShouldBe("d1");
        r.All(d => d.Score > 0).ShouldBeTrue();
    }

    [Fact]
    public async Task TopK_LimitsResults()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        var r = await sut.RetrieveAsync("fox", 2, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task FilterByMetadata_ReturnsOnlyMatching()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        var r = await sut.RetrieveAsync("renard", 10,
            filters: new Dictionary<string, object?> { ["lang"] = "fr" },
            ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBeGreaterThan(0);
        r.All(d => d.Id == "d4").ShouldBeTrue();
    }

    [Fact]
    public async Task NoMatch_ReturnsEmpty()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        var r = await sut.RetrieveAsync("xyzzyplugh", 5, ct: TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryBeforeIndex_ReturnsEmpty()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        var r = await sut.RetrieveAsync("anything", 5, ct: TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
    }

    [Fact]
    public async Task MalformedQuery_ReturnsEmpty()
    {
        using var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        var r = await sut.RetrieveAsync("", 5, ct: TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
    }

    [Fact]
    public async Task FilesystemDirectory_CreatesIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), "ampelum-bm25-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sut = new BM25Retriever(new BM25IndexOptions { IndexDirectory = path });
            await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
            var r = await sut.RetrieveAsync("fox", 3, ct: TestContext.Current.CancellationToken);
            r.Count.ShouldBeGreaterThan(0);
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    [Fact]
    public async Task Dispose_ReleasesResources()
    {
        var sut = new BM25Retriever(new BM25IndexOptions());
        await sut.IndexAsync(Corpus(), TestContext.Current.CancellationToken);
        Should.NotThrow(() => sut.Dispose());
    }

    [Fact]
    public void Options_DefaultsAreApplied()
    {
        var o = new BM25IndexOptions();
        o.K1.ShouldBe(1.2f);
        o.B.ShouldBe(0.75f);
        o.IndexDirectory.ShouldBeNull();
    }
}
