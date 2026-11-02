#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;
using ContractsErrorCodes = SignalGrid.Ampelum.Host.Contracts.ErrorCodes;
using HostErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;
using HostSseEvents = SignalGrid.Ampelum.Host.Sse.SseEvents;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class ContractRoundTripTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task OpenApi_ContainsExpectedOperationNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/openapi/v1.json", ct);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(ct);

        // Operation names registered on the route builders
        var expectedOperations = new[]
        {
            "ForwardModule", "StreamModule", "ListModules",
            "StartOptimize", "GetOptimizeStatus", "StreamOptimize",
            "GetPrompts", "ListAudit",
        };

        foreach (var op in expectedOperations)
            content.Contains(op).ShouldBeTrue($"Operation '{op}' not found in /openapi/v1.json");
    }

    [Fact]
    public void ForwardRequest_JsonContexts_ProduceSameOutput()
    {
        var obj = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                ["Q"] = JsonSerializer.SerializeToElement("hello"),
                ["context"] = JsonSerializer.SerializeToElement(42),
            });

        var fromHost = JsonSerializer.Serialize(obj, AmpelumHostJsonContext.Default.ForwardRequest);
        var fromContracts = JsonSerializer.Serialize(obj, AmpelumHostContractsJsonContext.Default.ForwardRequest);
        fromHost.ShouldBe(fromContracts);
    }

    [Fact]
    public void ErrorResponse_JsonContexts_ProduceSameOutput()
    {
        var obj = new ErrorResponse("trace-1", "bad_request", "Something went wrong.", null);
        var fromHost = JsonSerializer.Serialize(obj, AmpelumHostJsonContext.Default.ErrorResponse);
        var fromContracts = JsonSerializer.Serialize(obj, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        fromHost.ShouldBe(fromContracts);
    }

    [Fact]
    public void OptimizeRunStatus_JsonContexts_ProduceSameOutput()
    {
        var now = DateTimeOffset.UtcNow;
        var obj = new OptimizeRunStatus("run-1", "Mod", "bootstrap_few_shot",
            "succeeded", now, now.AddSeconds(5), 3, 3, "v-abc", null);
        var fromHost = JsonSerializer.Serialize(obj, AmpelumHostJsonContext.Default.OptimizeRunStatus);
        var fromContracts = JsonSerializer.Serialize(obj, AmpelumHostContractsJsonContext.Default.OptimizeRunStatus);
        fromHost.ShouldBe(fromContracts);
    }

    [Fact]
    public void ErrorCodes_ContractsAndHost_SetEquals()
    {
        var contractsCodes = new HashSet<string>(ContractsErrorCodes.All);
        var hostCodes = new HashSet<string>(HostErrorCodes.All);
        contractsCodes.SetEquals(hostCodes).ShouldBeTrue(
            $"Contracts codes: [{string.Join(", ", contractsCodes)}]\nHost codes: [{string.Join(", ", hostCodes)}]");
    }

    [Fact]
    public void SseEventNames_ForwardAll_MatchesHostSseEventsConstants()
    {
        var contractsForwardEvents = new HashSet<string>(SseEventNames.Forward.All);
        // Host SseEvents constants for forward stream
        var hostForwardEvents = new HashSet<string>
        {
            HostSseEvents.Token,
            HostSseEvents.Partial,
            HostSseEvents.Final,
            HostSseEvents.Error,
            HostSseEvents.Done,
        };
        contractsForwardEvents.SetEquals(hostForwardEvents).ShouldBeTrue(
            $"Contracts forward events: [{string.Join(", ", contractsForwardEvents)}]\n" +
            $"Host forward events: [{string.Join(", ", hostForwardEvents)}]");
    }

    [Fact]
    public void SseEventNames_OptimizeAll_MatchesHostSseEventsConstants()
    {
        var contractsOptimizeEvents = new HashSet<string>(SseEventNames.Optimize.All);
        var hostOptimizeEvents = new HashSet<string>
        {
            HostSseEvents.Progress,
            HostSseEvents.Trial,
            HostSseEvents.Final,
            HostSseEvents.Error,
            HostSseEvents.Done,
        };
        contractsOptimizeEvents.SetEquals(hostOptimizeEvents).ShouldBeTrue();
    }
}
