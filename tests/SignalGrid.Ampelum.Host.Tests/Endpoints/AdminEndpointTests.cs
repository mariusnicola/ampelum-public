#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class AdminEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task POST_admin_rebuild_404_WhenAdminDisabled()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.PostAsync("/admin/rebuild-index", null, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
