#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class OptimizeEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task POST_optimize_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new OptimizeRequest(
            OptimizerType: "MIPROv2",
            TrainingSet: new InlineExamples(new List<DemoExample>()),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/optimize/DoesNotExist", request,
            AmpelumHostContractsJsonContext.Default.OptimizeRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
    }

    [Fact]
    public async Task POST_optimize_422_UnknownOptimizerType()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new OptimizeRequest(
            OptimizerType: "UnknownOptimizer",
            TrainingSet: new InlineExamples(new List<DemoExample>()),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/optimize/Fake", request,
            AmpelumHostContractsJsonContext.Default.OptimizeRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("signature_validation_failed");
    }

    [Fact]
    public async Task GET_optimize_runs_404_UnknownRunId()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.GetAsync("/optimize/runs/run-doesnotexist", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("run_not_found");
    }
}
