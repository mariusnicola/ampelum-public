#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class StreamEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task POST_stream_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/DoesNotExist/stream", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_stream_200_ReturnsSseEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## A ## ]]\nHello");

        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/stream", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
    }
}
