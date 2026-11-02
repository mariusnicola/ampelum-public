using System.Net;
using System.Net.Http;
using System.Reflection;
using SignalGrid.Ampelum.Tools;
using SignalGrid.Ampelum.Tools.WebSearch;
using Shouldly;
using Xunit;

namespace Ampelum.Tools.WebSearch.Tests;

public sealed class WebSearchToolTests
{
    [Fact]
    public async Task WebSearchTool_Returns_TopThreeResults_GivenJsonStub()
    {
        const string json = """
        {
          "web": {
            "results": [
              { "title": "A", "url": "https://a", "description": "alpha" },
              { "title": "B", "url": "https://b", "description": "beta" },
              { "title": "C", "url": "https://c", "description": "gamma" },
              { "title": "D", "url": "https://d", "description": "delta" }
            ]
          }
        }
        """;
        using var http = new HttpClient(new StubHandler(json));
        var provider = new BraveSearchProvider(http, "test-key");
        var tool = new WebSearchTool(provider, "anything", topK: 3);

        var results = await tool.InvokeAsync(TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
        results[0].Title.ShouldBe("A");
        results[0].Url.ShouldBe("https://a");
        results[0].Snippet.ShouldBe("alpha");
        results[2].Title.ShouldBe("C");
    }

    [Fact]
    public async Task WebSearchTool_Empty_WhenNoResultsField()
    {
        using var http = new HttpClient(new StubHandler("{}"));
        var provider = new BraveSearchProvider(http, "k");
        var tool = new WebSearchTool(provider, "q");
        var results = await tool.InvokeAsync(TestContext.Current.CancellationToken);
        results.ShouldBeEmpty();
    }

    [Fact]
    public void WebSearchTool_Has_ToolCategoryExternal()
    {
        var attr = typeof(WebSearchTool).GetCustomAttribute<ToolCategoryAttribute>();
        attr.ShouldNotBeNull();
        attr!.Kind.ShouldBe(ToolCategoryKind.External);
    }

    [Fact]
    public void WebSearchTool_NameAndDescription_AreNonEmpty()
    {
        var tool = new WebSearchTool(new ZeroProvider(), "q", 2);
        tool.Name.ShouldBe("web_search");
        tool.Description.ShouldNotBeNullOrWhiteSpace();
        tool.Query.ShouldBe("q");
        tool.TopK.ShouldBe(2);
    }

    [Fact]
    public async Task WebSearchTool_DefaultTopK_IsThree()
    {
        var p = new ZeroProvider();
        var tool = new WebSearchTool(p, "q");
        await tool.InvokeAsync(TestContext.Current.CancellationToken);
        p.LastTopK.ShouldBe(3);
    }

    sealed class ZeroProvider : IWebSearchProvider
    {
        public int LastTopK;
        public ValueTask<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct)
        {
            LastTopK = topK;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }
    }

    sealed class StubHandler : HttpMessageHandler
    {
        readonly string _payload;
        public StubHandler(string payload) => _payload = payload;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Contains("X-Subscription-Token").ShouldBeTrue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
