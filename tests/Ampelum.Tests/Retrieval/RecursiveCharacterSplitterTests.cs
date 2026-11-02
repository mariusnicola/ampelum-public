using SignalGrid.Ampelum.Retrieval.Loaders;
using SignalGrid.Ampelum.Retrieval.Splitters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class RecursiveCharacterSplitterTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var sut = new RecursiveCharacterSplitter();
        var chunks = sut.Split("short", new SplitterContext { ChunkSize = 100 }).ToList();
        chunks.Count.ShouldBe(1);
        chunks[0].Text.ShouldBe("short");
    }

    [Fact]
    public void Split_LongText_ChunksRespectSize_AndOverlap()
    {
        var text = string.Concat(Enumerable.Repeat("abcdefghij", 50)); // 500 chars
        var sut = new RecursiveCharacterSplitter();
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 100, ChunkOverlap = 20 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.All(c => c.Text.Length <= 100).ShouldBeTrue();
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            chunks[i + 1].StartOffset.ShouldBeLessThan(chunks[i].EndOffset);
        }
    }

    [Fact]
    public void Split_PrefersParagraphBoundaries()
    {
        var text = "Para one is short.\n\nPara two is also short.\n\nPara three.";
        var sut = new RecursiveCharacterSplitter();
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 25, ChunkOverlap = 0 }).ToList();
        chunks.Any(c => c.Text.Contains("Para one")).ShouldBeTrue();
        chunks.Any(c => c.Text.Contains("Para two")).ShouldBeTrue();
    }

    [Fact]
    public void Split_ChunkIdsAreDeterministic()
    {
        var sut = new RecursiveCharacterSplitter();
        var ctx = new SplitterContext { ChunkSize = 50, SourceDocumentId = "doc1" };
        var first = sut.Split("hello world this is a longer text that needs splitting", ctx).ToList();
        var second = sut.Split("hello world this is a longer text that needs splitting", ctx).ToList();
        first.Select(c => (c.StartOffset, c.EndOffset)).ShouldBe(second.Select(c => (c.StartOffset, c.EndOffset)));
    }

    [Fact]
    public void Split_NoSeparatorMatch_HardChunks()
    {
        var text = new string('A', 250);
        var sut = new RecursiveCharacterSplitter(["X", ""]); // X never matches => fallback to hard
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 100, ChunkOverlap = 10 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.All(c => c.Text.Length <= 100).ShouldBeTrue();
    }

    [Fact]
    public void Split_CustomSeparators_Respected()
    {
        var text = "aa|bb|cc|dd|ee|ff";
        var sut = new RecursiveCharacterSplitter(["|", ""]);
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 5, ChunkOverlap = 0 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Split_WordLongerThanChunkSize_RecursiveSplit()
    {
        // "aaaa" (4 chars) exceeds ChunkSize=3; the space separator splits ["aaaa","bbb"],
        // "aaaa" gets flushed as a too-large buffer and SplitInternal recurses into it.
        var sut = new RecursiveCharacterSplitter([" ", ""]);
        var chunks = sut.Split("aaaa bbb", new SplitterContext { ChunkSize = 3, ChunkOverlap = 0 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.Any(c => c.Text == "aaa").ShouldBeTrue();
    }

    [Fact]
    public void SplitDocuments_Extension_ChunksAndPropagatesMetadata()
    {
        var docs = new[]
        {
            new Document("d1", string.Concat(Enumerable.Repeat("xyzab", 50)), new Dictionary<string, object?> { ["src"] = "a" }),
        };
        var sut = new RecursiveCharacterSplitter();
        var split = sut.SplitDocuments(docs, new SplitterContext { ChunkSize = 100, ChunkOverlap = 0 }).ToList();
        split.Count.ShouldBeGreaterThan(1);
        split[0].Id.ShouldStartWith("d1#");
        split[0].Metadata["src"].ShouldBe("a");
        split[0].Metadata.ContainsKey("chunk_start").ShouldBeTrue();
    }
}
