using System.Text;
using SignalGrid.Ampelum.Retrieval.Loaders;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class HtmlLoaderTests
{
    private static Stream Of(string html) => new MemoryStream(Encoding.UTF8.GetBytes(html));

    [Fact]
    public async Task Load_StripsScriptAndStyle()
    {
        var html = "<html><head><style>x{}</style></head><body><script>bad()</script><h1>Title</h1><p>Body</p></body></html>";
        var sut = new HtmlLoader();
        var docs = new List<Document>();
        await foreach (var d in sut.LoadAsync(Of(html), new DocumentLoadContext { SourceUri = "p.html" }, TestContext.Current.CancellationToken))
        {
            docs.Add(d);
        }
        docs.Count.ShouldBe(1);
        docs[0].Text.ShouldContain("Title");
        docs[0].Text.ShouldContain("Body");
        docs[0].Text.ShouldNotContain("bad()");
        docs[0].Text.ShouldNotContain("x{}");
        docs[0].Metadata["source"].ShouldBe("p.html");
        docs[0].Id.ShouldBe("p.html");
    }

    [Fact]
    public async Task Load_PreservesHeadingsAsMetadata()
    {
        var html = "<html><body><h1>Hi</h1><h2>Sub</h2></body></html>";
        var sut = new HtmlLoader();
        Document? doc = null;
        await foreach (var d in sut.LoadAsync(Of(html), new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            doc = d;
            break;
        }
        var headings = (IReadOnlyList<string>)doc!.Metadata["headings"]!;
        headings.ShouldBe(["Hi", "Sub"]);
    }

    [Fact]
    public async Task Load_MalformedHtml_DoesNotThrow_ReturnsBestEffort()
    {
        var html = "<html<body><p>partial";
        var sut = new HtmlLoader();
        var docs = new List<Document>();
        await foreach (var d in sut.LoadAsync(Of(html), new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            docs.Add(d);
        }
        docs.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Load_NoSourceUri_IdIsHtml()
    {
        var sut = new HtmlLoader();
        Document? d = null;
        await foreach (var doc in sut.LoadAsync(Of("<p>x</p>"), new DocumentLoadContext(), TestContext.Current.CancellationToken))
        {
            d = doc;
            break;
        }
        d!.Id.ShouldBe("html");
    }
}
