#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.ErrorHandling;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;
using ContractErrorCodes = SignalGrid.Ampelum.Host.Contracts.ErrorCodes;
using InternalErrorCodes = SignalGrid.Ampelum.Host.ErrorHandling.ErrorCodes;

namespace SignalGrid.Ampelum.Host.Tests.ErrorHandling;

public class ProblemDetailsMiddlewareTests : IAsyncLifetime
{
    readonly HostFactory _factory = new();

    [Fact]
    public void ErrorCodes_MirrorCount_Matches_Contracts()
    {
        InternalErrorCodes.All.Count.ShouldBe(ContractErrorCodes.All.Count);
    }

    [Fact]
    public void ErrorCodes_MirrorNames_Match_Contracts()
    {
        for (var i = 0; i < InternalErrorCodes.All.Count; i++)
        {
            InternalErrorCodes.All[i].ShouldBe(ContractErrorCodes.All[i]);
        }
    }

    [Fact]
    public async Task Middleware_Is_Registered()
    {
        var client = _factory.CreateClient();

        // Test that the application starts successfully with the middleware registered
        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    public async ValueTask InitializeAsync() => await _factory.InitializeAsync();
    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
}
