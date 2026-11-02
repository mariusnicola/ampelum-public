using SignalGrid.Ampelum.Retrieval.Splitters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class SentenceSplitterTests
{
    [Fact]
    public void Split_KeepsSentencesIntact()
    {
        var text = "First sentence. Second sentence! Third? Fourth done.";
        var sut = new SentenceSplitter();
        var chunks = sut.Split(text, new SplitterContext { ChunkSize = 25 }).ToList();
        chunks.All(c => c.Text.Length <= 60).ShouldBeTrue();
        chunks.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void Split_ShortText_OneChunk()
    {
        var sut = new SentenceSplitter();
        var chunks = sut.Split("Hi.", new SplitterContext { ChunkSize = 100 }).ToList();
        chunks.Count.ShouldBe(1);
    }

    [Fact]
    public void Split_LongSingleSentence_OneChunk()
    {
        var sut = new SentenceSplitter();
        var chunks = sut.Split("one long sentence with no punctuation", new SplitterContext { ChunkSize = 1000 }).ToList();
        chunks.Count.ShouldBe(1);
    }
}
