using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SignalGrid.Ampelum.Batch;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Batch;

[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class OpenAIBatchSubmitterTests
{
    static IReadOnlyList<ChatMessage> Msgs(string user) => [new ChatMessage(ChatRole.User, user)];

    static OpenAIBatchSubmitter MakeSubmitter(MockHttpHandler handler, string? apiKey = "sk-test")
    {
        var http = new HttpClient(handler);
        return new OpenAIBatchSubmitter(http, apiKey);
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

    static (OpenAIBatchSubmitter Submitter, BodyCapturingHandler Capture) MakeCapturingSubmitter()
    {
        var capture = new BodyCapturingHandler(new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"file-123\"}")
            .When(HttpMethod.Post, "batches", HttpStatusCode.OK, "{\"id\":\"batch_abc\"}"));
        return (new OpenAIBatchSubmitter(new HttpClient(capture), "sk-test"), capture);
    }

    // The first body is the multipart upload; its only JSON line is the single JSONL request.
    static string? UploadedModel(BodyCapturingHandler capture)
    {
        var jsonLine = capture.Bodies[0]
            .Split('\n', StringSplitOptions.TrimEntries)
            .Single(l => l.StartsWith("{\"custom_id\"", StringComparison.Ordinal));
        return JsonNode.Parse(jsonLine)!["body"]!["model"]!.GetValue<string>();
    }

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new OpenAIBatchSubmitter(null!));
    }

    [Fact]
    public void Ctor_SetsDefaultBaseAddress_AndAuthHeader()
    {
        var http = new HttpClient(new MockHttpHandler());
        _ = new OpenAIBatchSubmitter(http, "sk-abc");
        http.BaseAddress!.ToString().ShouldBe("https://api.openai.com/v1/");
        http.DefaultRequestHeaders.Authorization!.Scheme.ShouldBe("Bearer");
        http.DefaultRequestHeaders.Authorization.Parameter.ShouldBe("sk-abc");
    }

    [Fact]
    public void Ctor_PreservesPresetBaseAddress_AndAuth()
    {
        var http = new HttpClient(new MockHttpHandler())
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "preset");
        _ = new OpenAIBatchSubmitter(http, "ignored");
        http.BaseAddress.ToString().ShouldBe("https://openrouter.ai/api/v1/");
        http.DefaultRequestHeaders.Authorization.Parameter.ShouldBe("preset");
    }

    [Fact]
    public void Ctor_NullApiKey_DoesNotSetHeader()
    {
        var http = new HttpClient(new MockHttpHandler());
        _ = new OpenAIBatchSubmitter(http, apiKey: null);
        http.DefaultRequestHeaders.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task SubmitAsync_NullRequests_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.SubmitAsync(null!, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_EmptyRequests_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentException>(() => s.SubmitAsync(Array.Empty<BatchRequest>(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_NoModelId_ThrowsArgumentException()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        var req = new BatchRequest("c1", Msgs("hi"));

        var ex = await Should.ThrowAsync<ArgumentException>(() => s.SubmitAsync([req], null, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ModelId is required");
    }

    [Fact]
    public async Task SubmitAsync_RequestOptionsWithoutModel_UseBatchModel()
    {
        var (s, capture) = MakeCapturingSubmitter();
        var req = new BatchRequest("c1", Msgs("hi"), new ChatOptions { Temperature = 0.2f });

        var handle = await s.SubmitAsync([req], new ChatOptions { ModelId = "batch-model" }, TestContext.Current.CancellationToken);

        handle.Id.ShouldBe("batch_abc");
        UploadedModel(capture).ShouldBe("batch-model");
    }

    [Fact]
    public async Task SubmitAsync_RequestWithOwnModel_KeepsItOverBatchModel()
    {
        var (s, capture) = MakeCapturingSubmitter();
        var req = new BatchRequest("c1", Msgs("hi"), new ChatOptions { ModelId = "own-model" });

        await s.SubmitAsync([req], new ChatOptions { ModelId = "batch-model" }, TestContext.Current.CancellationToken);

        UploadedModel(capture).ShouldBe("own-model");
    }

    [Fact]
    public async Task SubmitAsync_RequestOptionsWithoutModel_CallerOptionsNotMutated()
    {
        var (s, _) = MakeCapturingSubmitter();
        var callerOptions = new ChatOptions { Temperature = 0.2f };
        var batchOptions = new ChatOptions { ModelId = "batch-model" };
        var req = new BatchRequest("c1", Msgs("hi"), callerOptions);

        await s.SubmitAsync([req], batchOptions, TestContext.Current.CancellationToken);

        callerOptions.ModelId.ShouldBeNull();
        req.Options.ShouldBeSameAs(callerOptions);
        batchOptions.ModelId.ShouldBe("batch-model");
    }

    [Fact]
    public async Task SubmitAsync_HappyPath_UploadsFileAndCreatesBatch()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"file-123\"}")
            .When(HttpMethod.Post, "batches", HttpStatusCode.OK, "{\"id\":\"batch_abc\"}");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 0, 0, 0, TimeSpan.Zero));
        var http = new HttpClient(handler);
        var s = new OpenAIBatchSubmitter(http, "sk", clock);

        var req = new BatchRequest("c1", Msgs("hi"),
            new ChatOptions { ModelId = "gpt-test", MaxOutputTokens = 100, Temperature = 0.5f });
        var handle = await s.SubmitAsync([req], options: null, TestContext.Current.CancellationToken);

        handle.Id.ShouldBe("batch_abc");
        handle.Provider.ShouldBe(OpenAIBatchSubmitter.ProviderName);
        handle.SubmittedAt.ShouldBe(clock.GetUtcNow());
        handler.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SubmitAsync_UsesDefaultOptionsWhenItemHasNone()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"f\"}")
            .When(HttpMethod.Post, "batches", HttpStatusCode.OK, "{\"id\":\"b\"}");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        var handle = await s.SubmitAsync([req], new ChatOptions { ModelId = "default-model" }, TestContext.Current.CancellationToken);
        handle.Id.ShouldBe("b");
    }

    [Fact]
    public async Task SubmitAsync_NullItemInList_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"f\"}");
        var s = MakeSubmitter(handler);
        await Should.ThrowAsync<ArgumentNullException>(() => s.SubmitAsync([null!], null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_FileUploadFails_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.InternalServerError, "boom");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        await Should.ThrowAsync<HttpRequestException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_FileUploadResponseMissingId_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"unexpected\":1}");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_BatchCreateMissingId_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"f\"}")
            .When(HttpMethod.Post, "batches", HttpStatusCode.OK, "{\"x\":1}");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_FileUploadEmptyBody_Throws()
    {
        // ReadFromJsonAsync<JsonNode> on "null" returns null.
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SubmitAsync_BatchCreateEmptyBody_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Post, "files", HttpStatusCode.OK, "{\"id\":\"f\"}")
            .When(HttpMethod.Post, "batches", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        var req = new BatchRequest("c1", Msgs("hi"));
        await Should.ThrowAsync<InvalidOperationException>(() => s.SubmitAsync([req], new ChatOptions { ModelId = "test-model" }, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("in_progress", BatchState.InProgress)]
    [InlineData("validating", BatchState.InProgress)]
    [InlineData("completed", BatchState.Completed)]
    [InlineData("failed", BatchState.Failed)]
    [InlineData("cancelled", BatchState.Cancelled)]
    [InlineData("cancelling", BatchState.Cancelled)]
    [InlineData("expired", BatchState.Expired)]
    public async Task GetStatusAsync_MapsAllStateStrings(string raw, BatchState expected)
    {
        var body = $"{{\"status\":\"{raw}\",\"request_counts\":{{\"completed\":3,\"failed\":1,\"total\":5}}}}";
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b1", HttpStatusCode.OK, body);
        var s = MakeSubmitter(handler);
        var status = await s.GetStatusAsync(new BatchHandle("b1", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        status.State.ShouldBe(expected);
        status.CompletedCount.ShouldBe(3);
        status.FailedCount.ShouldBe(1);
        status.TotalCount.ShouldBe(5);
    }

    [Fact]
    public async Task GetStatusAsync_MissingFieldsDefaultZero_AndDefaultStateInProgress()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b1", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        var status = await s.GetStatusAsync(new BatchHandle("b1", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        status.State.ShouldBe(BatchState.InProgress);
        status.CompletedCount.ShouldBe(0);
        status.FailedCount.ShouldBe(0);
        status.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetStatusAsync_NullHandle_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.GetStatusAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_NullJsonBody_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b1", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        await Should.ThrowAsync<InvalidOperationException>(() => s.GetStatusAsync(new BatchHandle("b1", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamResultsAsync_ParsesSuccessAndError()
    {
        var jsonl = string.Join('\n',
            "{\"custom_id\":\"c1\",\"response\":{\"body\":{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}}}",
            "{\"custom_id\":\"c2\",\"error\":{\"message\":\"rate limit\"}}",
            "",
            "{\"custom_id\":\"c3\",\"response\":{\"body\":{\"choices\":[{\"message\":{\"content\":\"world\"}}]}}}");
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b1", HttpStatusCode.OK, "{\"output_file_id\":\"out-1\"}")
            .When(HttpMethod.Get, "files/out-1/content", HttpStatusCode.OK, jsonl, "application/jsonl");
        var s = MakeSubmitter(handler);

        var items = new List<BatchResultItem>();
        await foreach (var item in s.StreamResultsAsync(new BatchHandle("b1", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken))
            items.Add(item);

        items.Count.ShouldBe(3);
        items[0].CustomId.ShouldBe("c1");
        items[0].Response!.Text.ShouldBe("hello");
        items[1].CustomId.ShouldBe("c2");
        items[1].ErrorMessage.ShouldBe("rate limit");
        items[2].CustomId.ShouldBe("c3");
        items[2].Response!.Text.ShouldBe("world");
    }

    [Fact]
    public async Task StreamResultsAsync_HandlesMissingFieldsGracefully()
    {
        // each line exercises a different missing-field branch
        var jsonl = string.Join('\n',
            "{}",                                               // no custom_id, no result
            "{\"custom_id\":\"a\",\"response\":{}}",            // no body
            "{\"custom_id\":\"b\",\"response\":{\"body\":{}}}",  // no choices
            "{\"custom_id\":\"c\",\"response\":{\"body\":{\"choices\":[]}}}", // empty choices
            "{\"custom_id\":\"d\",\"response\":{\"body\":{\"choices\":[{}]}}}", // choice without message
            "{\"custom_id\":\"e\",\"error\":null}",              // null error -> goes to no response branch
            "{\"custom_id\":\"f\",\"error\":\"raw\"}");          // error not object -> uses raw text
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b", HttpStatusCode.OK, "{\"output_file_id\":\"out\"}")
            .When(HttpMethod.Get, "files/out/content", HttpStatusCode.OK, jsonl);
        var s = MakeSubmitter(handler);

        var items = new List<BatchResultItem>();
        await foreach (var item in s.StreamResultsAsync(new BatchHandle("b", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken))
            items.Add(item);

        items.Count.ShouldBe(7);
        items[0].ErrorMessage.ShouldBe("no response");
        items[1].ErrorMessage.ShouldBe("no body");
        items[2].ErrorMessage.ShouldBe("no choices");
        items[3].ErrorMessage.ShouldBe("no choices");
        items[4].Response!.Text.ShouldBe("");
        items[5].ErrorMessage.ShouldBe("no response");
        items[6].ErrorMessage.ShouldBe("\"raw\"");
    }

    [Fact]
    public async Task StreamResultsAsync_NoOutputFileId_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in s.StreamResultsAsync(new BatchHandle("b", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task StreamResultsAsync_NullJson_Throws()
    {
        var handler = new MockHttpHandler()
            .When(HttpMethod.Get, "batches/b", HttpStatusCode.OK, "null");
        var s = MakeSubmitter(handler);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in s.StreamResultsAsync(new BatchHandle("b", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken)) { }
        });
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
            .When(HttpMethod.Post, "batches/b/cancel", HttpStatusCode.OK, "{}");
        var s = MakeSubmitter(handler);
        await s.CancelAsync(new BatchHandle("b", "openai", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        handler.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CancelAsync_NullHandle_Throws()
    {
        var s = MakeSubmitter(new MockHttpHandler());
        await Should.ThrowAsync<ArgumentNullException>(() => s.CancelAsync(null!, TestContext.Current.CancellationToken));
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    readonly DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}
