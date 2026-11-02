#pragma warning disable AMPELUM_V09_PREVIEW
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Host.ModuleRegistry;

namespace SignalGrid.Ampelum.Host.Tests.Fixtures;

public sealed class HostFactoryTests : IAsyncLifetime
{
    readonly HostFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void HostFactory_StartsWithout_Throwing()
    {
        var client = _factory.CreateClient();
        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task HostFactory_GetHealthz_Returns200()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        ((int)resp.StatusCode).ShouldBe(200);
    }

    [Fact]
    public void HostFactory_IModuleRegistry_IsFrozen_AfterBuild()
    {
        var registry = _factory.Services.GetRequiredService<IModuleRegistry>();
        registry.IsFrozen.ShouldBeTrue();
    }
}
