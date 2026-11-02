using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Safety.Moderation;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Safety.Moderation;

public sealed class OpenAiModeratorTests
{
    sealed class StubHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls;
        public string? LastAuth;
        public string? LastBody;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuth = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _responder(request);
        }
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task ModerateAsync_flagged_response_returns_flagged_true_and_categories()
    {
        var handler = new StubHandler(_ => Json("""{"results":[{"flagged":true,"categories":{"hate":true,"violence":false}}]}"""));
        var http = new HttpClient(handler);
        var sut = new OpenAiModerator(http, "sk-test");
        var result = await sut.ModerateAsync("bad text", TestContext.Current.CancellationToken);
        result.Flagged.ShouldBeTrue();
        result.Categories.ShouldContain("hate");
        result.Categories.ShouldNotContain("violence");
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ModerateAsync_clean_response_returns_flagged_false()
    {
        var handler = new StubHandler(_ => Json("""{"results":[{"flagged":false,"categories":{"hate":false}}]}"""));
        var sut = new OpenAiModerator(new HttpClient(handler), "sk-test");
        var result = await sut.ModerateAsync("hi", TestContext.Current.CancellationToken);
        result.Flagged.ShouldBeFalse();
        result.Categories.ShouldBeEmpty();
    }

    [Fact]
    public async Task ModerateAsync_caches_repeat_queries()
    {
        var handler = new StubHandler(_ => Json("""{"results":[{"flagged":false,"categories":{}}]}"""));
        var sut = new OpenAiModerator(new HttpClient(handler), "sk-test");
        await sut.ModerateAsync("same", TestContext.Current.CancellationToken);
        await sut.ModerateAsync("same", TestContext.Current.CancellationToken);
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ModerateAsync_uses_custom_endpoint_when_supplied()
    {
        Uri? seen = null;
        var handler = new StubHandler(req => { seen = req.RequestUri; return Json("""{"results":[{"flagged":false,"categories":{}}]}"""); });
        var sut = new OpenAiModerator(new HttpClient(handler), "sk-test", new Uri("https://custom.example/v1/moderations"));
        await sut.ModerateAsync("x", TestContext.Current.CancellationToken);
        seen!.ToString().ShouldBe("https://custom.example/v1/moderations");
    }

    [Fact]
    public async Task ModerateAsync_throws_on_non_success_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var sut = new OpenAiModerator(new HttpClient(handler), "sk-test");
        await Should.ThrowAsync<HttpRequestException>(
            async () => await sut.ModerateAsync("x", TestContext.Current.CancellationToken));
    }
}
