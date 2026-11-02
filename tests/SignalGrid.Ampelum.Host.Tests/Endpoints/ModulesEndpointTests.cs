#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class ModulesEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GET_modules_Returns_RegistryContents()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/modules", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ModuleListResponse>(json, AmpelumHostContractsJsonContext.Default.ModuleListResponse);
        body.ShouldNotBeNull();
        body.Modules.ShouldNotBeEmpty();
        body.Modules.Any(m => m.Name == "Fake").ShouldBeTrue();
    }

    [Fact]
    public async Task GET_modules_moduleName_200_KnownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/modules/Fake", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ModuleDescriptor>(json, AmpelumHostContractsJsonContext.Default.ModuleDescriptor);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("Fake");
        body.SupportsStreaming.ShouldBeTrue();
        response.Headers.TryGetValues("X-Ampelum-Trace-Id", out var traceIds).ShouldBeTrue();
        traceIds!.First().Length.ShouldBe(32);
    }

    [Fact]
    public async Task GET_modules_moduleName_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/modules/DoesNotExist", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
        body.TraceId.Length.ShouldBe(32);
    }
}
