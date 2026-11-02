using Xunit;
using System.Net;
using System.Text;
using SignalGrid.Ampelum.Observability.LangSmith;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ampelum.Observability.LangSmith.Tests;

public sealed class LangSmithClientTests
{
    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            if (request.Content is { } c) LastBody = await c.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    static LangSmithClient Build(StubHandler handler, LangSmithCredentials? creds = null)
    {
        var http = new HttpClient(handler);
        return new LangSmithClient(http, Options.Create(creds ?? new LangSmithCredentials { ApiKey = "k", ProjectName = "p" }));
    }

    [Fact]
    public async Task SubmitAsync_PostsToRunsBatchWithHeaders()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler);
        await client.SubmitAsync(new[] { Sample() }, TestContext.Current.CancellationToken);
        handler.Last!.Method.ShouldBe(HttpMethod.Post);
        handler.Last.RequestUri!.ToString().ShouldEndWith("runs/batch");
        handler.Last.Headers.GetValues("X-API-Key").ShouldContain("k");
        handler.Last.Headers.GetValues("x-project-name").ShouldContain("p");
        handler.LastBody!.ShouldContain("\"post\"");
    }

    [Fact]
    public async Task SubmitAsync_WrapsErrorsWithoutLeakingApiKey()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = Build(handler);
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => client.SubmitAsync(new[] { Sample() }, TestContext.Current.CancellationToken));
        ex.Message.ShouldNotContain("k");
        ex.Message.ShouldContain("p");
    }

    [Fact]
    public async Task SubmitAsync_NoOpOnEmptyBatch()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler);
        await client.SubmitAsync(Array.Empty<LangSmithRun>(), TestContext.Current.CancellationToken);
        handler.Last.ShouldBeNull();
    }

    [Fact]
    public void SubmitAsync_ThrowsOnNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Build(handler);
        Should.Throw<ArgumentNullException>(() => client.SubmitAsync(null!, default));
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        var creds = Options.Create(new LangSmithCredentials { ApiKey = "k", ProjectName = "p" });
        Should.Throw<ArgumentNullException>(() => new LangSmithClient(null!, creds));
        Should.Throw<ArgumentNullException>(() => new LangSmithClient(new HttpClient(), null!));
    }

    [Fact]
    public void SerializeBatch_ProducesJson()
    {
        var batch = new LangSmithRunBatch { Post = new[] { Sample() } };
        var json = LangSmithClient.SerializeBatch(batch);
        json.ShouldContain("\"post\"");
        json.ShouldContain("\"name\"");
    }

    static LangSmithRun Sample() => new LangSmithRunMapper().Map(TraceRecordBuilder.Sample(), "p", true);
}
