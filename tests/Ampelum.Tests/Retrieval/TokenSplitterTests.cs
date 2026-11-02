using SignalGrid.Ampelum.Retrieval.Splitters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class TokenSplitterTests
{
    [Fact]
    public void Split_RespectsTokenBudget()
    {
        var text = string.Concat(Enumerable.Repeat("token ", 100));
        var sut = new TokenSplitter();
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 20, ChunkOverlap = 5 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.All(c => (int)c.Metadata["token_count"]! <= 20).ShouldBeTrue();
    }

    [Fact]
    public void Split_SmallText_OneChunk()
    {
        var sut = new TokenSplitter();
        var chunks = sut.Split("hi", new SplitterContext { ChunkSize = 100 }).ToList();
        chunks.Count.ShouldBe(1);
        ((int)chunks[0].Metadata["token_count"]!).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Split_TerminatesAtExactBoundary()
    {
        var text = string.Concat(Enumerable.Repeat("a ", 30));
        var sut = new TokenSplitter();
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 10, ChunkOverlap = 0 }).ToList();
        chunks.Count.ShouldBeGreaterThan(1);
    }
}
