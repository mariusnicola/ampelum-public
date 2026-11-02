#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class ForwardEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task POST_modules_forward_200_HappyPath()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.FakeChatClient.EnqueueText("[[ ## A ## ]]\nHello World");

        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("What is 2+2?") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ForwardResponse>(json, AmpelumHostContractsJsonContext.Default.ForwardResponse);
        body.ShouldNotBeNull();
        body.Outputs.ShouldContainKey("A");
        body.Outputs["A"].GetString().ShouldBe("Hello World");
        body.TraceId.Length.ShouldBe(32);
        response.Headers.TryGetValues("X-Ampelum-Trace-Id", out var traceIds).ShouldBeTrue();
        traceIds!.First().ShouldBe(body.TraceId);
    }

    [Fact]
    public async Task POST_modules_forward_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/DoesNotExist/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
        body.TraceId.Length.ShouldBe(32);
    }

    [Fact]
    public async Task POST_modules_forward_400_InvalidJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        var response = await _factory.HttpClient.PostAsync("/modules/Fake/forward", content, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task POST_modules_forward_422_MissingRequiredInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("signature_validation_failed");
    }
}
