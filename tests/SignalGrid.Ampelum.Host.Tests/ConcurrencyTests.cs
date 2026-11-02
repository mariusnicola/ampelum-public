#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum.Audit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class ConcurrencyTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    static ForwardRequest MakeRequest(string q) =>
        new(Inputs: new Dictionary<string, JsonElement>
            { ["Q"] = JsonSerializer.SerializeToElement(q) });

    [Fact]
    public async Task Parallel50SseStreams_ChainIntegrityPreserved()
    {
        const int N = 50;
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < N; i++)
            _factory.FakeChatClient.EnqueueText($"[[ ## A ## ]]\nStream{i}");

        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            var resp = await _factory.HttpClient.PostAsJsonAsync(
                "/modules/Fake/stream", MakeRequest($"S{i}"),
                AmpelumHostContractsJsonContext.Default.ForwardRequest,
                cancellationToken: ct);
            // Drain the stream to completion
            await resp.Content.ReadAsStringAsync(ct);
            return resp;
        });

        var responses = await Task.WhenAll(tasks);
        responses.ShouldAllBe(r => r.IsSuccessStatusCode);

        var sink = _factory.Services.GetRequiredService<IAuditSink>();
        var result = await AuditChainVerifier.VerifyAsync(sink, ct);
        result.IsValid.ShouldBeTrue(result.Reason);
    }

    [Fact]
    public async Task Parallel10PromptVersionCreates_AllInserted()
    {
        const int N = 10;
        var ct = TestContext.Current.CancellationToken;

        var req = new CreatePromptVersionRequest(
            DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExample>>
            {
                ["default"] = new List<DemoExample>
                {
                    new(
                        new Dictionary<string, JsonElement> { ["Q"] = JsonSerializer.SerializeToElement("hi") },
                        new Dictionary<string, JsonElement> { ["Answer"] = JsonSerializer.SerializeToElement("bye") })
                }
            },
            InstructionOverrides: new Dictionary<string, string>());

        var tasks = Enumerable.Range(0, N).Select(_ =>
            _factory.HttpClient.PostAsJsonAsync(
                "/prompts/Fake", req,
                AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest,
                cancellationToken: ct));

        var responses = await Task.WhenAll(tasks);
        // Each create should succeed
        var successCount = responses.Count(r => r.IsSuccessStatusCode);
        successCount.ShouldBe(N);

        // Verify the versions list has at least N entries
        var listResp = await _factory.HttpClient.GetAsync("/prompts/Fake", ct);
        var list = await listResp.Content.ReadFromJsonAsync<PromptVersionList>(
            AmpelumHostContractsJsonContext.Default.PromptVersionList, ct);
        list!.Versions.Count.ShouldBeGreaterThanOrEqualTo(N);
    }

    [Fact]
    public async Task Parallel100Forwards_AuditChainIntact_TraceIdsDistinct()
    {
        const int N = 100;
        var ct = TestContext.Current.CancellationToken;
        var sink = _factory.Services.GetRequiredService<IAuditSink>();

        // Enqueue N responses (one per parallel forward)
        for (var i = 0; i < N; i++)
            _factory.FakeChatClient.EnqueueText($"[[ ## A ## ]]\nResult{i}");

        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            var request = new ForwardRequest(
                Inputs: new Dictionary<string, JsonElement>
                {
                    { "Q", JsonSerializer.SerializeToElement($"test{i}") }
                });
            var resp = await _factory.HttpClient.PostAsJsonAsync(
                "/modules/Fake/forward", request,
                AmpelumHostContractsJsonContext.Default.ForwardRequest,
                cancellationToken: ct);
            return resp;
        });

        var responses = await Task.WhenAll(tasks);
        responses.ShouldAllBe(r => r.IsSuccessStatusCode);

        // All 100 responses must have distinct TraceIds
        var traceIds = new HashSet<string>();
        foreach (var r in responses)
        {
            var json = await r.Content.ReadAsStringAsync(ct);
            var body = JsonSerializer.Deserialize<ForwardResponse>(json,
                AmpelumHostContractsJsonContext.Default.ForwardResponse);
            body.ShouldNotBeNull();
            traceIds.Add(body.TraceId).ShouldBeTrue($"Duplicate TraceId: {body.TraceId}");
        }

        // Verify the full audit chain is intact (100 entries, hash-linked)
        var result = await AuditChainVerifier.VerifyAsync(sink, ct);
        result.IsValid.ShouldBeTrue(result.Reason);
    }

    [Fact]
    public async Task SingleForward_HappyPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = _factory.Services.GetRequiredService<IAuditSink>();

        // Clear any pre-existing audit entries
        await foreach (var _ in sink.EnumerateAsync(ct))
            ;

        _factory.FakeChatClient.EnqueueText("[[ ## A ## ]]\nResult1");

        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ForwardResponse>(json, AmpelumHostContractsJsonContext.Default.ForwardResponse);
        body.ShouldNotBeNull();
        body.Outputs.ShouldContainKey("A");
        body.Outputs["A"].GetString().ShouldBe("Result1");
        body.TraceId.Length.ShouldBe(32);

        var result = await AuditChainVerifier.VerifyAsync(sink, ct);
        result.IsValid.ShouldBeTrue(result.Reason);
    }
}