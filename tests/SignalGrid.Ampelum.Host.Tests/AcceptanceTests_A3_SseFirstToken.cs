#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class AcceptanceTests_A3_SseFirstToken : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task SseStream_FirstTokenArrivesWithin1500ms()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## A ## ]]\nHello");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("fast") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync(
            "/modules/Fake/stream", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buf = new byte[16];
        var bytesRead = await stream.ReadAsync(buf.AsMemory(), ct);
        sw.Stop();

        bytesRead.ShouldBeGreaterThan(0);
        sw.ElapsedMilliseconds.ShouldBeLessThan(1500);
    }
}