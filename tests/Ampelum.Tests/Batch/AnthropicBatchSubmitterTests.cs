using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using SignalGrid.Ampelum.Batch;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Batch;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AnthropicBatchSubmitterTests
{
    static IReadOnlyList<ChatMessage> Msgs(params (ChatRole role, string text)[] items)
        => items.Select(x => new ChatMessage(x.role, x.text)).ToList();

    static AnthropicBatchSubmitter MakeSubmitter(MockHttpHandler handler, string? apiKey = "k1")
    {
        var http = new HttpClient(handler);
        return new AnthropicBatchSubmitter(http, apiKey);
    }

    // The request content is disposed after sending, so the body is read while it is in flight.
    sealed class BodyCapturingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    static (AnthropicBatchSubmitter Submitter, BodyCapturingHandler Capture) MakeCapturingSubmitter()
    {
        var capture = new BodyCapturingHandler(new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.OK, "{\"id\":\"b\"}"));
        return (new AnthropicBatchSubmitter(new HttpClient(capture), "k1"), capture);
    }

    static string? SentModel(BodyCapturingHandler capture)
    {
        var body = JsonNode.Parse(capture.Bodies.ShouldHaveSingleItem())!;
        return body["requests"]![0]!["params"]!["model"]!.GetValue<string>();
    }

    [Fact]
    public void Ctor_NullHttp_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new AnthropicBatchSubmitter(null!));
    }

    [Fact]
    public void Ctor_SetsBaseAddressAndHeaders()
    {
        var http = new HttpClient(new MockHttpHandler());
        _ = new AnthropicBatchSubmitter(http, "k");
        http.BaseAddress!.ToString().ShouldBe("https://api.anthropic.com/");
        http.DefaultRequestHeaders.GetValues("anthropic-version").ShouldContain(AnthropicBatchSubmitter.ApiVersion);
        http.DefaultRequestHeaders.GetValues("x-api-key").ShouldContain("k");
    }

    [Fact]
    public void Ctor_PreservesPresetBaseAddressAndHeaders()
    {
        var http = new HttpClient(new MockHttpHandler())
        {
            BaseAddress = new Uri("https://proxy.example.com/"),
        };
        http.DefaultRequestHeaders.Add("anthropic-version", "preset");
        http.DefaultRequestHeaders.Add("x-api-key", "preset-k");
        _ = new AnthropicBatchSubmitter(http, "ignored");
        http.BaseAddress.ToString().ShouldBe("https://proxy.example.com/");
        http.DefaultRequestHeaders.GetValues("anthropic-version").ShouldContain("preset");
        http.DefaultRequestHeaders.GetValues("x-api-key").ShouldContain("preset-k");
    }

    [Fact]
    public void Ctor_NullKey_DoesNotAddHeader()
    {
        var http = new HttpClient(new MockHttpHandler());
        _ = new AnthropicBatchSubmitter(http, apiKey: null);
        http.DefaultRequestHeaders.Contains("x-api-key").ShouldBeFalse();
    }

    [Fact]
    public async Task SubmitAsync_Validates()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.SubmitAsync(null!, null, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => s.SubmitAsync(Array.Empty<BatchRequest>(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_HappyPath_PostsAndReturnsHandle()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.OK, "{\"id\":\"msgbatch_1\"}");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 1, 0, 0, TimeSpan.Zero));
        var http = new HttpClient(handler);
        var s = new AnthropicBatchSubmitter(http, "k", clock);

        var msgs = Msgs(
            (ChatRole.System, "be brief"),
            (ChatRole.System, "in english"),
            (ChatRole.User, "hi"),
            (ChatRole.Assistant, "hello"));
        var req = new BatchRequest("c1", msgs, new ChatOptions { ModelId = "claude-test", MaxOutputTokens = 50, Temperature = 0.2f });
        var handle = await s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken);

        handle.Id.ShouldBe("msgbatch_1");
        handle.Provider.ShouldBe(AnthropicBatchSubmitter.ProviderName);
        handle.SubmittedAt.ShouldBe(clock.GetUtcNow());
    }

    [Fact]
    public async Task SubmitAsync_DefaultOptionsApplied_NoSystem_NoTemperature()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.OK, "{\"id\":\"b\"}");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")));
        await s.SubmitAsync([req], options: new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubmitAsync_NoModelId_ThrowsArgumentException()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")));
        var ex = await Should.ThrowAsync<ArgumentException>(() => s.SubmitAsync([req], null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("ModelId is required");
    }

    [Fact]
    public async Task SubmitAsync_RequestOptionsWithoutModel_UseBatchModel()
    {
        var (s, capture) = MakeCapturingSubmitter();
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")), new ChatOptions { Temperature = 0.2f });

        var handle = await s.SubmitAsync([req], new ChatOptions { ModelId = "batch-model" }, TestContext.Current.CancellationToken);

        handle.Id.ShouldBe("b");
        SentModel(capture).ShouldBe("batch-model");
    }

    [Fact]
    public async Task SubmitAsync_RequestWithOwnModel_KeepsItOverBatchModel()
    {
        var (s, capture) = MakeCapturingSubmitter();
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")), new ChatOptions { ModelId = "own-model" });

        await s.SubmitAsync([req], new ChatOptions { ModelId = "batch-model" }, TestContext.Current.CancellationToken);

        SentModel(capture).ShouldBe("own-model");
    }

    [Fact]
    public async Task SubmitAsync_RequestOptionsWithoutModel_CallerOptionsNotMutated()
    {
        var (s, _) = MakeCapturingSubmitter();
        var callerOptions = new ChatOptions { Temperature = 0.2f };
        var batchOptions = new ChatOptions { ModelId = "batch-model" };
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")), callerOptions);

        await s.SubmitAsync([req], batchOptions, TestContext.Current.CancellationToken);

        callerOptions.ModelId.ShouldBeNull();
        req.Options.ShouldBeSameAs(callerOptions);
        batchOptions.ModelId.ShouldBe("batch-model");
    }

    [Fact]
    public async Task SubmitAsync_RequestOptionsWithoutModel_NoBatchModel_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")), new ChatOptions { Temperature = 0.2f });

        var ex = await Should.ThrowAsync<ArgumentException>(() => s.SubmitAsync([req], null, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ModelId is required");
    }

    [Fact]
    public async Task SubmitAsync_NullItem_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.SubmitAsync([null!], null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_HttpFails_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.InternalServerError, "boom");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")));
        await Should.ThrowAsync<HttpRequestException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_NullJson_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_MissingId_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs((ChatRole.User, "hi")));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("in_progress", BatchState.InProgress)]
    [InlineData("ended", BatchState.Completed)]
    [InlineData("canceling", BatchState.Cancelled)]
    [InlineData("canceled", BatchState.Cancelled)]
    public async Task GetStatusAsync_MapsState(string raw, BatchState expected)
    {
        var body = $"{{\"processing_status\":\"{raw}\",\"request_counts\":{{\"succeeded\":2,\"errored\":1,\"canceled\":0,\"expired\":0,\"processing\":3}}}}";
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "v1/messages/batches/b1", HttpStatusCode.OK, body);
        var s = MakeSubmitter(handler);
        var status = await s.GetStatusAsync(new BatchHandle("b1", "anthropic", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        status.State.ShouldBe(expected);
        status.CompletedCount.ShouldBe(3); // 2 + 1 + 0 + 0
        status.FailedCount.ShouldBe(1);
        status.TotalCount.ShouldBe(6); // completed + processing
    }

    [Fact]
    public async Task GetStatusAsync_MissingCountsDefaultsZero_AndDefaultState()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "v1/messages/batches/b1", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        var status = await s.GetStatusAsync(new BatchHandle("b1", "anthropic", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        status.State.ShouldBe(BatchState.InProgress);
        status.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatusAsync_NullJson_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "v1/messages/batches/b", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        await Should.ThrowAsync<InvalidOperationException>(() => s.GetStatusAsync(new BatchHandle("b", "a", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_NullHandle_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.GetStatusAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamResultsAsync_ParsesSuccessAndError()
    {
        var jsonl = string.Join('\n',
            "{\"custom_id\":\"c1\",\"result\":{\"type\":\"succeeded\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello \"},{\"type\":\"text\",\"text\":\"world\"},{\"type\":\"tool_use\"}]}}}",
            "",
            "{\"custom_id\":\"c2\",\"result\":{\"type\":\"errored\",\"error\":{\"message\":\"bad\"}}}",
            "{\"custom_id\":\"c3\",\"result\":{\"type\":\"expired\"}}",
            "{\"custom_id\":\"c4\"}",
            "{\"custom_id\":\"c5\",\"result\":{\"type\":\"succeeded\"}}",
            "{\"custom_id\":\"c6\",\"result\":{\"type\":\"succeeded\",\"message\":{}}}");
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "v1/messages/batches/b/results", HttpStatusCode.OK, jsonl);
        var s = MakeSubmitter(handler);

        var items = new List<BatchResultItem>();
        await foreach (var i in s.StreamResultsAsync(new BatchHandle("b", "anthropic", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken))
            items.Add(i);

        items.Count.ShouldBe(6);
        items[0].Response!.Text.ShouldBe("hello world");
        items[1].ErrorMessage.ShouldBe("bad");
        items[2].ErrorMessage.ShouldBe("expired");
        items[3].ErrorMessage.ShouldBe("no result");
        items[4].ErrorMessage.ShouldBe("no content");
        items[5].ErrorMessage.ShouldBe("no content");
    }

    [Fact]
    public async Task StreamResultsAsync_NullHandle_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in s.StreamResultsAsync(null!, TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task CancelAsync_Posts()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "v1/messages/batches/b/cancel", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        await s.CancelAsync(new BatchHandle("b", "anthropic", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        handler.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CancelAsync_NullHandle_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.CancelAsync(null!, TestContext.Current.CancellationToken));
    }
}
