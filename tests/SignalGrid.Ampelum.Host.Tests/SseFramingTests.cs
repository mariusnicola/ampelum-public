#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class SseFramingTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    static async Task<List<string>> ReadRawLinesAsync(
        HttpResponseMessage response, int limit, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null && lines.Count < limit)
            lines.Add(line);
        return lines;
    }

    static ForwardRequest MakeForwardRequest(string q = "hello") =>
        new(Inputs: new Dictionary<string, JsonElement>
        {
            ["Q"] = JsonSerializer.SerializeToElement(q)
        });

    HttpRequestMessage MakeSseGetRequest(string url, string? lastEventId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept", "text/event-stream");
        if (lastEventId is not null)
            req.Headers.Add("Last-Event-ID", lastEventId);
        return req;
    }

    [Fact]
    public async Task StreamResponse_HasRequiredHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## Answer ## ]]\nHi");

        var response = await _factory.HttpClient.PostAsJsonAsync(
            "/modules/Fake/stream", MakeForwardRequest(),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        response.Headers.TryGetValues("Cache-Control", out var cacheControl).ShouldBeTrue();
        string.Join(",", cacheControl!).ShouldContain("no-cache");
        response.Headers.TryGetValues("X-Accel-Buffering", out var accelBuf).ShouldBeTrue();
        accelBuf!.First().ShouldBe("no");
    }

    [Fact]
    public async Task StreamResponse_EventDoneIsAlwaysLast()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## Answer ## ]]\nResult");

        var response = await _factory.HttpClient.PostAsJsonAsync(
            "/modules/Fake/stream", MakeForwardRequest("test"),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        var lines = await ReadRawLinesAsync(response, 100, ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        var eventLines = lines.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).ToList();
        eventLines.ShouldNotBeEmpty();
        eventLines.Last().ShouldBe("event: done");
    }

    [Fact]
    public async Task StreamResponse_FirstByteArrivesWithin1500ms()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## Answer ## ]]\nQuick");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _factory.HttpClient.PostAsJsonAsync(
            "/modules/Fake/stream", MakeForwardRequest("fast"),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        // Receive at least the initial keep-alive comment written by SseWriter.StartAsync
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buf = new byte[16];
        var bytesRead = await stream.ReadAsync(buf.AsMemory(), ct);
        sw.Stop();

        bytesRead.ShouldBeGreaterThan(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(1500);
    }

    [Fact]
    public async Task OptimizeStream_KeepAlive_EmitsCommentsDuringLongRunningStream()
    {
        var ct = TestContext.Current.CancellationToken;

        // Insert a run in "running" state — no actual runner will Complete() the bus, so stream stays open
        var runStore = _factory.Services.GetRequiredService<IOptimizerRunStore>();
        var runId = "keep-alive-framing-test";
        await runStore.InsertAsync(
            new OptimizerRunRow(runId, "Fake", OptimizerTypes.BootstrapFewShot,
                OptimizeRunState.Running, DateTimeOffset.UtcNow, null, 0, null, null, null),
            ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        using var req = MakeSseGetRequest($"/optimize/runs/{runId}/stream");
        var response = await _factory.HttpClient.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, ct);

        var keepAliveCount = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
            {
                if (line.StartsWith(": ", StringComparison.Ordinal) || line == ":")
                    keepAliveCount++;
                if (keepAliveCount >= 3) break;
            }
        }
        catch (OperationCanceledException) { }

        // KeepAliveSeconds=1 in ModuleTestFactory; read 4s total → expect ≥3 keep-alives
        keepAliveCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task OptimizeStream_LastEventIdHeader_IgnoredServerStartsFromBeginning()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## Answer ## ]]\nOk");

        // Start and complete a run
        var startResp = await _factory.HttpClient.PostAsJsonAsync(
            "/optimize/Fake",
            new OptimizeRequest(
                OptimizerType: OptimizerTypes.BootstrapFewShot,
                TrainingSet: new InlineExamples(new List<DemoExample>
                {
                    new(
                        new Dictionary<string, JsonElement>
                            { ["Q"] = JsonSerializer.SerializeToElement("Q") },
                        new Dictionary<string, JsonElement>
                            { ["A"] = JsonSerializer.SerializeToElement("A") })
                }),
                OptimizerOptions: new Dictionary<string, JsonElement>()),
            AmpelumHostContractsJsonContext.Default.OptimizeRequest, cancellationToken: ct);

        var handle = await startResp.Content.ReadFromJsonAsync<OptimizeRunHandle>(
            AmpelumHostContractsJsonContext.Default.OptimizeRunHandle, ct);
        var runId = handle!.RunId;

        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(50, ct);
            var statusResp = await _factory.HttpClient.GetAsync($"/optimize/runs/{runId}", ct);
            var s = await statusResp.Content.ReadFromJsonAsync<OptimizeRunStatus>(
                AmpelumHostContractsJsonContext.Default.OptimizeRunStatus, ct);
            if (s?.State is OptimizeRunState.Succeeded or OptimizeRunState.Failed
                or OptimizeRunState.Cancelled) break;
        }

        // Subscribe with Last-Event-ID: 999 — server ignores it, returns events from start
        using var req = MakeSseGetRequest($"/optimize/runs/{runId}/stream", lastEventId: "999");
        var streamResp = await _factory.HttpClient.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, ct);

        streamResp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var lines = await ReadRawLinesAsync(streamResp, 50, ct)
            .WaitAsync(TimeSpan.FromSeconds(5), ct);

        var eventLines = lines.Where(l => l.StartsWith("event:", StringComparison.Ordinal)).ToList();
        eventLines.ShouldNotBeEmpty();
        eventLines.ShouldContain("event: done");
    }
}
