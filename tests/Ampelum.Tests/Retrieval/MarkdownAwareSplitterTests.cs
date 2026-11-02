using SignalGrid.Ampelum.Retrieval.Splitters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class MarkdownAwareSplitterTests
{
    [Fact]
    public void Split_ChunksOnHeadingBoundaries_AndTagsHeadingPath()
    {
        var md = "# Top\nintro text\n## A\nalpha body\n## B\nbravo body";
        var sut = new MarkdownAwareSplitter();
        var chunks = sut.Split(md, new SplitterContext { ChunkSize = 1000 }).ToList();
        chunks.Count.ShouldBe(3);
        chunks[0].Metadata["heading_path"].ShouldBe("Top");
        chunks[1].Metadata["heading_path"].ShouldBe("Top > A");
        chunks[2].Metadata["heading_path"].ShouldBe("Top > B");
        chunks[1].Text.ShouldContain("alpha body");
    }

    [Fact]
    public void Split_NoHeadings_ReturnsOneChunk()
    {
        var md = "just paragraph text";
        var sut = new MarkdownAwareSplitter();
        var chunks = sut.Split(md, new SplitterContext { ChunkSize = 1000 }).ToList();
        chunks.Count.ShouldBe(1);
    }

    [Fact]
    public void Split_HeadingsAtSameLevel_DoNotNest()
    {
        var md = "# A\nx\n# B\ny";
        var sut = new MarkdownAwareSplitter();
        var chunks = sut.Split(md, new SplitterContext { ChunkSize = 1000 }).ToList();
        chunks.Count.ShouldBe(2);
        chunks[0].Metadata["heading_path"].ShouldBe("A");
        chunks[1].Metadata["heading_path"].ShouldBe("B");
    }
}
