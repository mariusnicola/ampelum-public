#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class OpenApiEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GET_openapi_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.GetAsync("/openapi/v1.json", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull();
    }

    [Fact]
    public async Task GET_openapi_ContainsForwardRoute()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.GetAsync("/openapi/v1.json", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(ct);
        content.ShouldNotBeNull();
        content.ShouldContain("ForwardModule");
    }
}
