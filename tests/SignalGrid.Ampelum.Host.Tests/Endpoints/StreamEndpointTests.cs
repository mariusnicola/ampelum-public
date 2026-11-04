#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class StreamEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    sealed record Frame(string Event, JsonElement Data);

    static ForwardRequest Request() => new(
        Inputs: new Dictionary<string, JsonElement> { { "Q", JsonSerializer.SerializeToElement("test") } });

    static async Task<(HttpResponseMessage Response, List<Frame> Frames)> PostStreamAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/modules/Fake/stream", Request(),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response, ParseFrames(body));
    }

    // Frames are separated by a blank line; lines starting with ':' are keep-alive comments.
    static List<Frame> ParseFrames(string body)
    {
        var frames = new List<Frame>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            var data = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line["event: ".Length..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data.Add(line["data: ".Length..]);
            }
            if (name is null) continue;
            frames.Add(new Frame(name, JsonDocument.Parse(string.Join("\n", data)).RootElement.Clone()));
        }
        return frames;
    }

    [Fact]
    public async Task POST_stream_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/DoesNotExist/stream", Request(),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_stream_200_SendsPartialsThenFinalThenDone()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueStreamingChunks("[[ ## A ## ]]\n", "Hel", "lo");

        var (response, frames) = await PostStreamAsync(_factory.HttpClient, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

        var names = frames.Select(f => f.Event).ToList();
        names.ShouldNotContain("error");
        names.Count(n => n == "final").ShouldBe(1);
        names[^2..].ShouldBe(["final", "done"]);
        names[..^2].ShouldAllBe(n => n == "partial");
        frames.Count.ShouldBeGreaterThan(2);

        var partials = frames.Where(f => f.Event == "partial").ToList();
        partials.ShouldAllBe(f => f.Data.GetProperty("field").GetString() == "A");
        // The streaming parser keeps the newline after the field marker in the running text; the final frame has it trimmed.
        partials[^1].Data.GetProperty("value").GetString()!.Trim().ShouldBe("Hello");

        var final = frames.Single(f => f.Event == "final").Data;
        final.GetProperty("outputs").GetProperty("A").GetString().ShouldBe("Hello");
        final.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        final.GetProperty("modelId").GetString().ShouldNotBeNullOrEmpty();
        final.GetProperty("usage").GetProperty("durationMs").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        frames[^1].Data.GetProperty("traceId").GetString().ShouldBe(final.GetProperty("traceId").GetString());
    }

    sealed class ShortCircuitHook : IHook
    {
        public int Order => 0;

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            ctx.Outputs = new Dictionary<string, object?> { ["A"] = "from hook" };
            return ValueTask.CompletedTask;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task POST_stream_HookShortCircuit_SendsFinalWithHookOutputsAndNoModelCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ModuleTestFactory(configureServices: s => s.AddSingleton<IHook>(new ShortCircuitHook()));
        await factory.InitializeAsync();

        var (response, frames) = await PostStreamAsync(factory.HttpClient, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        frames.Select(f => f.Event).ShouldBe(["final", "done"]);
        frames[0].Data.GetProperty("outputs").GetProperty("A").GetString().ShouldBe("from hook");
        factory.FakeChatClient.RecordedCalls.ShouldBeEmpty();
    }

    static async Task<string> CreateVersionAsync(HttpClient client, string instruction, CancellationToken ct)
    {
        var request = new CreatePromptVersionRequest(
            new Dictionary<string, IReadOnlyList<DemoExample>>(),
            new Dictionary<string, string> { ["default"] = instruction });
        var response = await client.PostAsJsonAsync("/prompts/Fake", request,
            AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.PromptVersion, ct))!.VersionId;
    }

    static async Task PromoteAsync(HttpClient client, string versionId, CancellationToken ct) =>
        (await client.PostAsJsonAsync("/prompts/Fake/promote", new PromoteRequest(versionId),
            AmpelumHostContractsJsonContext.Default.PromoteRequest, cancellationToken: ct)).EnsureSuccessStatusCode();

    static async Task<HttpResponseMessage> StreamWithVersionAsync(HttpClient client, string? versionId, CancellationToken ct) =>
        await client.PostAsJsonAsync("/modules/Fake/stream",
            new ForwardRequest(
                new Dictionary<string, JsonElement> { { "Q", JsonSerializer.SerializeToElement("test") } }, versionId),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

    static string SentText(FakeChatClient model, int call) =>
        string.Join("\n", model.RecordedCalls[call].Messages.Select(m => m.Text));

    [Fact]
    public async Task POST_stream_SendsThePromotedInstruction_AndFinalNamesTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Sentinel = "SENTINEL-STREAM-INSTRUCTION-3c9e";
        var versionId = await CreateVersionAsync(_factory.HttpClient, Sentinel, ct);
        await PromoteAsync(_factory.HttpClient, versionId, ct);
        _factory.FakeChatClient.EnqueueStreamingChunks("[[ ## A ## ]]\n", "Hel", "lo");

        var (response, frames) = await PostStreamAsync(_factory.HttpClient, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        SentText(_factory.FakeChatClient, 0).ShouldContain(Sentinel);
        frames.Single(f => f.Event == "final").Data.GetProperty("promptVersionId").GetString().ShouldBe(versionId);
    }

    [Fact]
    public async Task POST_stream_WithoutAVersion_SendsTheModulesOwnInstruction_AndFinalNamesNoVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Sentinel = "SENTINEL-STREAM-NOT-PROMOTED-77d1";
        await CreateVersionAsync(_factory.HttpClient, Sentinel, ct);
        _factory.FakeChatClient.EnqueueStreamingChunks("[[ ## A ## ]]\n", "Hello");

        var (_, frames) = await PostStreamAsync(_factory.HttpClient, ct);

        SentText(_factory.FakeChatClient, 0).ShouldNotContain(Sentinel);
        var final = frames.Single(f => f.Event == "final").Data;
        (final.TryGetProperty("promptVersionId", out var named) && named.ValueKind != JsonValueKind.Null).ShouldBeFalse();
    }

    [Fact]
    public async Task POST_stream_WithAnExplicitVersionId_SendsThatVersionEvenWhenItIsNotPromoted()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Sentinel = "SENTINEL-STREAM-EXPLICIT-a4f0";
        var versionId = await CreateVersionAsync(_factory.HttpClient, Sentinel, ct);
        _factory.FakeChatClient.EnqueueStreamingChunks("[[ ## A ## ]]\n", "Hello");

        using var response = await StreamWithVersionAsync(_factory.HttpClient, versionId, ct);
        var frames = ParseFrames(await response.Content.ReadAsStringAsync(ct));

        SentText(_factory.FakeChatClient, 0).ShouldContain(Sentinel);
        frames.Single(f => f.Event == "final").Data.GetProperty("promptVersionId").GetString().ShouldBe(versionId);
    }

    [Fact]
    public async Task POST_stream_WithAnUnknownVersionId_Answers404VersionNotFound_AndCallsNoModel()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await StreamWithVersionAsync(_factory.HttpClient, "v-missing", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/event-stream");
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct))!;
        error.Code.ShouldBe("version_not_found");
        _factory.FakeChatClient.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_stream_WithAVersionOfAnotherModule_Answers404VersionNotFound_AndCallsNoModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>().InsertChallengerAsync(
            new SignalGrid.Ampelum.Host.Persistence.PromptVersionRow(
                "v-of-other-module", "Other", PromptStatus.Challenger, DateTimeOffset.UtcNow, null, "/no/such/file.json", null),
            ct);

        using var response = await StreamWithVersionAsync(_factory.HttpClient, "v-of-other-module", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct))!;
        error.Code.ShouldBe("version_not_found");
        _factory.FakeChatClient.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_stream_WhenThePromotedVersionsFileIsGone_Answers400StreamFailed_AndCallsNoModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var versionId = await CreateVersionAsync(_factory.HttpClient, "Be brief.", ct);
        await PromoteAsync(_factory.HttpClient, versionId, ct);
        var row = (await _factory.Services.GetRequiredService<SignalGrid.Ampelum.Host.Persistence.IPromptVersionStore>()
            .TryGetByIdAsync(versionId, ct))!;
        File.Delete(row.FilePath);

        using var response = await StreamWithVersionAsync(_factory.HttpClient, null, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/event-stream");
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct))!;
        error.Message.ShouldStartWith("Stream failed:");
        _factory.FakeChatClient.RecordedCalls.ShouldBeEmpty();
    }

    sealed class FailingMidStreamChatClient(Exception failure) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "[[ ## A ## ]]\nHel");
            await Task.Yield();
            throw failure;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task POST_stream_ModelFailsMidStream_SendsErrorThenDoneAndNoFinal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ModuleTestFactory(chatClientOverride: new FailingMidStreamChatClient(new InvalidOperationException("model went away")));
        await factory.InitializeAsync();

        var (response, frames) = await PostStreamAsync(factory.HttpClient, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var names = frames.Select(f => f.Event).ToList();
        names[^2..].ShouldBe(["error", "done"]);
        names.ShouldNotContain("final");
        frames[^2].Data.GetProperty("message").GetString()!.ShouldContain("model went away");
    }

    [Fact]
    public async Task POST_stream_ModelTimesOutMidStream_SendsPartialThenErrorThenDone()
    {
        var ct = TestContext.Current.CancellationToken;
        // A model timeout surfaces as TaskCanceledException while the caller is still connected.
        await using var factory = new ModuleTestFactory(
            chatClientOverride: new FailingMidStreamChatClient(new TaskCanceledException("model timed out")));
        await factory.InitializeAsync();

        var (response, frames) = await PostStreamAsync(factory.HttpClient, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        frames.Select(f => f.Event).ShouldBe(["partial", "error", "done"]);
        frames[1].Data.GetProperty("message").GetString()!.ShouldContain("model timed out");
    }

    [Fact]
    public async Task POST_stream_CompletedTerminator_IsNotSentAsAPartial()
    {
        var ct = TestContext.Current.CancellationToken;
        // The terminator marker the recipe's streaming example ends with.
        _factory.FakeChatClient.EnqueueStreamingChunks("[[ ## A ## ]]\n", "hi", "\n[[ ## completed ## ]]");

        var (_, frames) = await PostStreamAsync(_factory.HttpClient, ct);

        var names = frames.Select(f => f.Event).ToList();
        names[^2..].ShouldBe(["final", "done"]);
        names.ShouldNotContain("error");
        frames.Where(f => f.Event == "partial").ShouldAllBe(f => f.Data.GetProperty("field").GetString() == "A");
        var outputs = frames.Single(f => f.Event == "final").Data.GetProperty("outputs");
        outputs.EnumerateObject().Select(p => p.Name).ShouldBe(["A"]);
        outputs.GetProperty("A").GetString().ShouldBe("hi");
    }

    [Fact]
    public async Task POST_stream_ModuleMappedWithoutStreaming_Is422()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ModuleTestFactory(supportsStreaming: false);
        await factory.InitializeAsync();

        var response = await factory.HttpClient.PostAsJsonAsync("/modules/Fake/stream", Request(),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, ct))!;
        error.Message.ShouldBe("Module does not support streaming.");
        factory.FakeChatClient.RecordedCalls.ShouldBeEmpty();
    }

    sealed class GatedChatClient(Task gate) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "[[ ## A ## ]]\nHel");
            await gate.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "lo");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task POST_stream_FirstPartialArrivesBeforeTheModelFinishes()
    {
        var test = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new ModuleTestFactory(chatClientOverride: new GatedChatClient(release.Task));
        await factory.InitializeAsync();
        // Only a guard so a buffering endpoint fails the test instead of hanging it; nothing asserts on elapsed time.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(test);
        guard.CancelAfter(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/modules/Fake/stream")
        {
            Content = JsonContent.Create(Request(), AmpelumHostContractsJsonContext.Default.ForwardRequest),
        };
        using var response = await factory.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        await using var body = await response.Content.ReadAsStreamAsync(guard.Token);
        using var reader = new StreamReader(body);

        // The model is still blocked on the gate, so this frame can only have been flushed early.
        var first = new List<string>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            if (line.StartsWith(':')) continue;
            if (line.Length == 0)
            {
                if (first.Count > 0) break;
                continue;
            }
            first.Add(line);
        }
        release.SetResult();

        first.ShouldContain("event: partial");
        var rest = await reader.ReadToEndAsync(guard.Token);
        var names = ParseFrames(string.Join("\n", first) + "\n\n" + rest).Select(f => f.Event).ToList();
        names[^2..].ShouldBe(["final", "done"]);
    }
}
