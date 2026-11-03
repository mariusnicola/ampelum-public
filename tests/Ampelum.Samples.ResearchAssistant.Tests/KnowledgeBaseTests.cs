using Shouldly;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Xunit;

namespace Ampelum.Samples.ResearchAssistant.Tests;

public sealed class KnowledgeBaseTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<KnowledgeBase> BuildFromAsync(
        Dictionary<string, string> files, ResearchOptions? options = null, IReranker? reranker = null)
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var (name, text) in files)
                await File.WriteAllTextAsync(Path.Combine(directory, name + ".md"), text, Ct);
            return await KnowledgeBase.BuildAsync(
                directory, new HashingEmbeddingGenerator(), options ?? new ResearchOptions(), reranker, Ct);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MarkdownFiles_AreLoadedAndSplitIntoChunksWithSourceAndTitle()
    {
        using var kb = await KnowledgeBase.BuildAsync(
            ResearchData.KnowledgeDirectory, new HashingEmbeddingGenerator(), new ResearchOptions(), ct: Ct);

        kb.Chunks.Select(c => c.Id).ShouldContain("solar-panels#0");
        kb.Chunks.Select(c => c.Id).ShouldContain("solar-panels#2");
        var yield = kb.Chunks.Single(c => c.Text.Contains("1,100 kWh per kW"));
        yield.Id.ShouldBe("solar-panels#1");
        yield.Metadata["source"].ShouldBe("solar-panels");
        yield.Metadata["title"].ShouldBe("Rooftop solar panels");
        kb.Chunks.ShouldAllBe(c => c.Text.Length <= 300);
    }

    [Fact]
    public async Task CrlfText_SplitsIntoTheSameChunksAsLfText()
    {
        var lf = "# Title\n\n" + string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} about solar panels and their yield."));
        var options = new ResearchOptions { ChunkSize = 120, ChunkOverlap = 0 };
        using var fromLf = await BuildFromAsync(new() { ["doc"] = lf }, options);
        using var fromCrlf = await BuildFromAsync(new() { ["doc"] = lf.Replace("\n", "\r\n") }, options);

        fromLf.Chunks.Count.ShouldBeGreaterThan(1);
        fromCrlf.Chunks.Select(c => (c.Id, c.Text)).ShouldBe(fromLf.Chunks.Select(c => (c.Id, c.Text)));
    }

    [Fact]
    public async Task SmallerChunkSize_ProducesMoreChunks()
    {
        var text = "# Title\n\n" + string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} about solar panels and their yield."));
        using var large = await BuildFromAsync(new() { ["doc"] = text }, new ResearchOptions { ChunkSize = 1000 });
        using var small = await BuildFromAsync(new() { ["doc"] = text }, new ResearchOptions { ChunkSize = 60, ChunkOverlap = 0 });

        large.Chunks.Count.ShouldBe(1);
        small.Chunks.Count.ShouldBeGreaterThan(3);
        small.Chunks.ShouldAllBe(c => c.Text.Length <= 60);
    }

    [Fact]
    public async Task Hybrid_FindsWhatOnlyKeywordsFindAndWhatOnlyVectorsFind()
    {
        // "panels" only matches the query "panel" after the embedder removes the plural; Lucene does not
        using var kb = await BuildFromAsync(new()
        {
            ["a"] = "Panels convert sunlight.",
            ["b"] = "The panel junction box holds the cable.",
            ["c"] = "Wind turbines need a mast.",
        });

        var keywords = await kb.Keywords.RetrieveAsync("panel", 3, ct: Ct);
        var vectors = await kb.Vectors.RetrieveAsync("panel", 1, ct: Ct);
        var hybrid = await kb.Hybrid.RetrieveAsync("panel", 3, ct: Ct);

        keywords.Select(d => d.Id).ShouldBe(["b#0"]);
        vectors.Select(d => d.Id).ShouldBe(["a#0"]);
        hybrid.Select(d => d.Id).Take(2).ShouldBe(["b#0", "a#0"]);
    }

    [Fact]
    public async Task Reranker_PutsTheChunkHoldingTheQueryPhraseAheadOfTheFusedLeader()
    {
        using var kb = await KnowledgeBase.BuildAsync(
            ResearchData.KnowledgeDirectory, new HashingEmbeddingGenerator(), new ResearchOptions(), ct: Ct);
        const string query = "kWh per year";

        var fused = await kb.Hybrid.RetrieveAsync(query, 6, ct: Ct);
        var reranked = await kb.SearchAsync(query, 6, Ct);

        // fusion ranks by position in two lists; the reranker also rewards the words sitting side by side
        fused[0].Id.ShouldBe("solar-panels#1");
        reranked[0].Id.ShouldBe("wind-turbines#0");
        reranked[0].Text.ShouldContain("kWh per year");
        reranked.Select(d => d.Id).ShouldBe(fused.Select(d => d.Id), ignoreOrder: true);
        reranked.Select(d => d.Score).ShouldBe(reranked.Select(d => d.Score).OrderByDescending(s => s));
    }

    [Fact]
    public async Task SearchAsync_PutsTheRelevantChunkFirstAndReturnsOnlyTopK()
    {
        using var kb = await KnowledgeBase.BuildAsync(
            ResearchData.KnowledgeDirectory, new HashingEmbeddingGenerator(), new ResearchOptions(), ct: Ct);

        var hits = await kb.SearchAsync("home battery module usable kWh", 2, Ct);

        hits.Count.ShouldBe(2);
        hits[0].Id.ShouldBe("battery-storage#0");
    }

    [Fact]
    public void OverlapScorer_PrefersMoreQueryTermsAndAdjacentPairs()
    {
        using var scorer = new OverlapScorer();

        var both = scorer.Score("battery capacity", "A battery has a capacity.");
        var one = scorer.Score("battery capacity", "A battery is heavy.");
        var adjacent = scorer.Score("solar array", "A solar array on the roof.");
        var apart = scorer.Score("solar array", "An array of cables next to solar lamps.");

        both.ShouldBeGreaterThan(one);
        adjacent.ShouldBeGreaterThan(apart);
        scorer.Score("the of", "anything").ShouldBe(0f);
    }

    [Fact]
    public async Task EmbeddingGenerator_GivesEqualVectorsForPluralAndSingular()
    {
        using var embedder = new HashingEmbeddingGenerator();

        var result = await embedder.GenerateAsync(["panel", "panels", "turbine"], cancellationToken: Ct);

        result[0].Vector.ToArray().ShouldBe(result[1].Vector.ToArray());
        result[0].Vector.ToArray().ShouldNotBe(result[2].Vector.ToArray());
    }
}
