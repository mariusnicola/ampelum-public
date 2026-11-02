using System.Net;
using System.Net.Http;
using System.Reflection;
using SignalGrid.Ampelum.Tools;
using SignalGrid.Ampelum.Tools.Wikipedia;
using Shouldly;
using Xunit;

namespace Ampelum.Tools.Wikipedia.Tests;

public sealed class WikipediaToolTests
{
    [Fact]
    public async Task Wikipedia_Returns_LeadParagraph_ForKnownTitle()
    {
        const string json = """
        {
          "title": "Albert Einstein",
          "extract": "Albert Einstein was a theoretical physicist.",
          "content_urls": { "desktop": { "page": "https://en.wikipedia.org/wiki/Albert_Einstein" } }
        }
        """;
        using var http = new HttpClient(new StubHandler(json, capture: out var captured));
        var tool = new WikipediaTool(http, "Albert Einstein");

        var summary = await tool.InvokeAsync(TestContext.Current.CancellationToken);

        summary.Title.ShouldBe("Albert Einstein");
        summary.Extract.ShouldStartWith("Albert Einstein was");
        summary.Url.ShouldBe("https://en.wikipedia.org/wiki/Albert_Einstein");
        captured.RequestUri!.AbsoluteUri.ShouldContain("Albert_Einstein");
    }

    [Fact]
    public async Task Wikipedia_FallsBackToInputTitle_OnEmptyPayload()
    {
        using var http = new HttpClient(new StubHandler("{}", capture: out _));
        var tool = new WikipediaTool(http, "Some Title");
        var s = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        s.Title.ShouldBe("Some Title");
        s.Extract.ShouldBe("");
        s.Url.ShouldBe("");
    }

    [Fact]
    public void Wikipedia_HasToolCategoryExternal()
    {
        var attr = typeof(WikipediaTool).GetCustomAttribute<ToolCategoryAttribute>();
        attr.ShouldNotBeNull();
        attr!.Kind.ShouldBe(ToolCategoryKind.External);
    }

    [Fact]
    public void Wikipedia_NameDescription_NonEmpty()
    {
        using var http = new HttpClient(new StubHandler("{}", out _));
        var tool = new WikipediaTool(http, "X");
        tool.Name.ShouldBe("wikipedia");
        tool.Description.ShouldNotBeNullOrWhiteSpace();
        tool.Title.ShouldBe("X");
    }

    sealed class StubHandler : HttpMessageHandler
    {
        readonly string _json;
        readonly CapturedRequest _capture;
        public StubHandler(string json, out CapturedRequest capture)
        {
            _json = json;
            _capture = new CapturedRequest();
            capture = _capture;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _capture.RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
    sealed class CapturedRequest { public Uri? RequestUri { get; set; } }
}
