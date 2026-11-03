using System.Net;
using System.Net.Http;
using System.Text;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class VoyageRerankerTests
{
    private sealed class StubHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastRequest = req;
            if (req.Content is not null)
                LastBody = await req.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static IReadOnlyList<RetrievedDocument> Docs(params string[] ids)
        => ids.Select(i => new RetrievedDocument(i, $"text {i}", 0, new Dictionary<string, object?>())).ToList();

    [Fact]
    public async Task Rerank_ReordersByVoyageScores()
    {
        var json = """{"data":[{"index":2,"relevance_score":0.95},{"index":0,"relevance_score":0.6},{"index":1,"relevance_score":0.2}]}""";
        var stub = new StubHandler(json);
        var sut = new VoyageReranker(new HttpClient(stub), new VoyageRerankerOptions { ApiKey = "key", Model = "caller-model" });
        var r = await sut.RerankAsync("q", Docs("a", "b", "c"), 3, TestContext.Current.CancellationToken);
        r.Select(d => d.Id).ShouldBe(["c", "a", "b"]);
        r[0].Score.ShouldBe(0.95, 0.0001);
        stub.LastBody.ShouldNotBeNull().ShouldContain("\"caller-model\"");
    }

    [Fact]
    public async Task Rerank_HttpError_Throws()
    {
        var stub = new StubHandler("""{"error":"x"}""", HttpStatusCode.InternalServerError);
        var sut = new VoyageReranker(new HttpClient(stub), new VoyageRerankerOptions { ApiKey = "k", Model = "test-model" });
        await Should.ThrowAsync<HttpRequestException>(() => sut.RerankAsync("q", Docs("a"), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rerank_EmptyCandidates_NoHttpCall()
    {
        var stub = new StubHandler("""{"data":[]}""");
        var sut = new VoyageReranker(new HttpClient(stub), new VoyageRerankerOptions { ApiKey = "k", Model = "test-model" });
        var r = await sut.RerankAsync("q", Array.Empty<RetrievedDocument>(), 5, TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
        stub.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Rerank_TopK_Truncates()
    {
        var json = """{"data":[{"index":0,"relevance_score":0.9},{"index":1,"relevance_score":0.5}]}""";
        var sut = new VoyageReranker(new HttpClient(new StubHandler(json)), new VoyageRerankerOptions { ApiKey = "k", Model = "test-model" });
        var r = await sut.RerankAsync("q", Docs("a", "b"), 1, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }

    [Fact]
    public void Options_ModelIsTheCallers_EndpointDefaultsToVoyage()
    {
        var o = new VoyageRerankerOptions { ApiKey = "k", Model = "test-model" };
        o.Model.ShouldBe("test-model");
        o.Endpoint.Host.ShouldContain("voyageai.com");
    }
}
