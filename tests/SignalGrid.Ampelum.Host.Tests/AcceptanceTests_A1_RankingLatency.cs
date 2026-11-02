#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class AcceptanceTests_A1_RankingLatency : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    static DemoExample MakeExample(int i) => new(
        new Dictionary<string, JsonElement>
            { ["Q"] = JsonSerializer.SerializeToElement($"Query {i}") },
        new Dictionary<string, JsonElement>
            { ["A"] = JsonSerializer.SerializeToElement($"Target {i}") });

    [Fact]
    public async Task RankingOptimizer_20Candidates_CompletesWithin5Seconds()
    {
        const int exampleCount = 20;
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < exampleCount; i++)
            _factory.FakeChatClient.EnqueueText($"[[ ## A ## ]]\nResult{i}");

        var examples = Enumerable.Range(0, exampleCount).Select(MakeExample).ToList();
        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(examples),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var sw = Stopwatch.StartNew();
        var response = await _factory.HttpClient.PostAsJsonAsync(
            "/optimize/Fake", request,
            AmpelumHostContractsJsonContext.Default.OptimizeRequest,
            cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        var handle = await response.Content.ReadFromJsonAsync<OptimizeRunHandle>(
            AmpelumHostContractsJsonContext.Default.OptimizeRunHandle, ct);
        handle.ShouldNotBeNull();
        handle.RunId.ShouldNotBeNullOrEmpty();

        OptimizeRunStatus? status = null;
        while (true)
        {
            var poll = await _factory.HttpClient.GetAsync(
                $"/optimize/runs/{handle.RunId}", ct);
            poll.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

            status = await poll.Content.ReadFromJsonAsync<OptimizeRunStatus>(
                AmpelumHostContractsJsonContext.Default.OptimizeRunStatus, ct);
            status.ShouldNotBeNull();

            if (status.State is OptimizeRunState.Succeeded or OptimizeRunState.Failed or OptimizeRunState.Cancelled)
                break;

            sw.Elapsed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
            await Task.Delay(50, ct);
        }

        sw.Elapsed.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
        status!.State.ShouldBe(OptimizeRunState.Succeeded);
    }
}