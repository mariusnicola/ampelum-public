using System.Net;
using System.Net.Http;
using System.Text;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class CohereRerankerTests
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
    public async Task Rerank_ReordersByCohereScores()
    {
        var json = """{"results":[{"index":2,"relevance_score":0.99},{"index":0,"relevance_score":0.5},{"index":1,"relevance_score":0.3}]}""";
        var stub = new StubHandler(json);
        var sut = new CohereReranker(new HttpClient(stub), new CohereRerankerOptions { ApiKey = "key", Model = "caller-model" });
        var r = await sut.RerankAsync("q", Docs("a", "b", "c"), 3, TestContext.Current.CancellationToken);
        r.Select(d => d.Id).ShouldBe(["c", "a", "b"]);
        r[0].Score.ShouldBe(0.99, 0.0001);
        stub.LastBody.ShouldNotBeNull().ShouldContain("\"caller-model\"");
    }

    [Fact]
    public async Task Rerank_TopK_Truncates()
    {
        var json = """{"results":[{"index":0,"relevance_score":0.9},{"index":1,"relevance_score":0.5}]}""";
        var sut = new CohereReranker(new HttpClient(new StubHandler(json)), new CohereRerankerOptions { ApiKey = "k", Model = "test-model" });
        var r = await sut.RerankAsync("q", Docs("a", "b"), 1, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
        r[0].Id.ShouldBe("a");
    }

    [Theory]
    [InlineData("""{"results":[{"index":7,"relevance_score":0.9}]}""")]
    [InlineData("""{"results":[{"index":-1,"relevance_score":0.9}]}""")]
    [InlineData("""{"results":[{"index":2,"relevance_score":0.9}]}""")]
    public async Task Rerank_AnIndexOutsideTheCandidates_ThrowsInvalidData_NamingIndexAndCount(string json)
    {
        var sut = new CohereReranker(new HttpClient(new StubHandler(json)), new CohereRerankerOptions { ApiKey = "k", Model = "m" });

        var ex = await Should.ThrowAsync<InvalidDataException>(() => sut.RerankAsync("q", Docs("a", "b"), 1, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("2 candidates");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"results":null}""")]
    public async Task Rerank_ABodyWithoutResults_ThrowsInvalidData(string json)
    {
        var sut = new CohereReranker(new HttpClient(new StubHandler(json)), new CohereRerankerOptions { ApiKey = "k", Model = "m" });

        await Should.ThrowAsync<InvalidDataException>(() => sut.RerankAsync("q", Docs("a"), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rerank_AnIndexBeyondTopK_IsNotChecked_BecauseItIsNotUsed()
    {
        var json = """{"results":[{"index":0,"relevance_score":0.9},{"index":9,"relevance_score":0.1}]}""";
        var sut = new CohereReranker(new HttpClient(new StubHandler(json)), new CohereRerankerOptions { ApiKey = "k", Model = "m" });

        var r = await sut.RerankAsync("q", Docs("a", "b"), 1, TestContext.Current.CancellationToken);

        r.Select(d => d.Id).ShouldBe(["a"]);
    }

    [Fact]
    public async Task Rerank_HttpError_Throws()
    {
        var stub = new StubHandler("""{"error":"forbidden"}""", HttpStatusCode.Forbidden);
        var sut = new CohereReranker(new HttpClient(stub), new CohereRerankerOptions { ApiKey = "k", Model = "test-model" });
        await Should.ThrowAsync<HttpRequestException>(() => sut.RerankAsync("q", Docs("a"), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rerank_EmptyCandidates_ReturnsEmpty_NoHttpCall()
    {
        var stub = new StubHandler("""{"results":[]}""");
        var sut = new CohereReranker(new HttpClient(stub), new CohereRerankerOptions { ApiKey = "k", Model = "test-model" });
        var r = await sut.RerankAsync("q", Array.Empty<RetrievedDocument>(), 5, TestContext.Current.CancellationToken);
        r.ShouldBeEmpty();
        stub.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Rerank_SetsBearerAuthHeader()
    {
        var json = """{"results":[{"index":0,"relevance_score":0.5}]}""";
        var stub = new StubHandler(json);
        var sut = new CohereReranker(new HttpClient(stub), new CohereRerankerOptions { ApiKey = "secret", Model = "test-model" });
        await sut.RerankAsync("q", Docs("a"), 1, TestContext.Current.CancellationToken);
        stub.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        stub.LastRequest.Headers.Authorization!.Parameter.ShouldBe("secret");
    }

    [Fact]
    public void Options_ModelIsTheCallers_EndpointDefaultsToCohere()
    {
        var o = new CohereRerankerOptions { ApiKey = "k", Model = "test-model" };
        o.Model.ShouldBe("test-model");
        o.Endpoint.ToString().ShouldContain("cohere.com");
    }
}
