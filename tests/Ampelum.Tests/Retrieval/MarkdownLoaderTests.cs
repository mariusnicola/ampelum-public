using System.Text;
using SignalGrid.Ampelum.Retrieval.Loaders;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class MarkdownLoaderTests
{
    private static Stream Of(string md) => new MemoryStream(Encoding.UTF8.GetBytes(md));

    [Fact]
    public async Task Load_PreservesHeadingAnchorsAsMetadata()
    {
        var md = "# Top\n\nbody1\n\n## Sub\n\nbody2";
        var sut = new MarkdownLoader();
        Document? doc = null;
        await foreach (var d in sut.LoadAsync(Of(md), new DocumentLoadContext { SourceUri = "x.md" }, TestContext.Current.CancellationToken))
        {
            doc = d;
            break;
        }
        var headings = (IReadOnlyList<string>)doc!.Metadata["headings"]!;
        headings.ShouldBe(["Top", "Sub"]);
        doc.Text.ShouldContain("body1");
        doc.Text.ShouldContain("body2");
        doc.Metadata["source"].ShouldBe("x.md");
        doc.Id.ShouldBe("x.md");
    }

    [Fact]
    public async Task Load_EmptyDocument_EmitsSingleEmpty()
    {
        var sut = new MarkdownLoader();
        Document? doc = null;
        await foreach (var d in sut.LoadAsync(Of(""), new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            doc = d;
            break;
        }
        doc!.Text.ShouldBeEmpty();
        doc.Id.ShouldBe("md");
    }

    [Fact]
    public async Task Load_NoHeadings_EmptyHeadingList()
    {
        var sut = new MarkdownLoader();
        Document? doc = null;
        await foreach (var d in sut.LoadAsync(Of("plain text"), new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            doc = d;
            break;
        }
        ((IReadOnlyList<string>)doc!.Metadata["headings"]!).ShouldBeEmpty();
    }
}
