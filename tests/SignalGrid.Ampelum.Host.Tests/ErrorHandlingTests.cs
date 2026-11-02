#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ErrorHandling;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests;

public sealed class ErrorHandlingTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ForwardModule_NotFound_404_ModuleNotFound()
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
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("module_not_found");
    }

    [Fact]
    public async Task StreamModule_NotFound_404_ModuleNotFound()
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
    public async Task ForwardModule_BadJson_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        var response = await _factory.HttpClient.PostAsync("/modules/Fake/forward", content, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task StreamModule_BadJson_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        var response = await _factory.HttpClient.PostAsync("/modules/Fake/stream", content, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task ForwardModule_MissingRequiredField_422_SignatureValidationFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("signature_validation_failed");
    }

    [Fact]
    public async Task StreamModule_MissingRequiredField_422_SignatureValidationFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/stream", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.UnprocessableEntity);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("signature_validation_failed");
    }

    [Fact]
    public async Task OptimizeModule_NotFound_404_ModuleNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(new List<DemoExample>()),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/optimize/DoesNotExist", request,
            AmpelumHostContractsJsonContext.Default.OptimizeRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("module_not_found");
    }

    [Fact]
    public async Task OptimizeModule_InvalidOptimizerType_422_SignatureValidationFailed()
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
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("signature_validation_failed");
    }

    [Fact]
    public async Task OptimizeModule_BadJson_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        var response = await _factory.HttpClient.PostAsync("/optimize/Fake", content, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task OptimizeModule_MissingRequiredField_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.PostAsJsonAsync("/optimize/Fake", null,
            AmpelumHostContractsJsonContext.Default.OptimizeRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task OptimizeStatus_NotFound_404_RunNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/optimize/runs/nonexistent", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("run_not_found");
    }

    [Fact]
    public async Task OptimizeStream_NotFound_404_RunNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/optimize/runs/nonexistent/stream", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("run_not_found");
    }

    [Fact]
    public async Task OptimizeStream_InvalidLastEventIdHeader_Ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new HttpRequestMessage(HttpMethod.Get, "/optimize/runs/nonexistent/stream")
        {
            Headers = { { "Last-Event-ID", "999" } }
        };
        var response = await _factory.HttpClient.SendAsync(request, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForwardModule_InvalidQueryParams_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/forward?invalid=param", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StreamModule_UnknownQueryParams_Ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new ForwardRequest(
            Inputs: new Dictionary<string, JsonElement>
            {
                { "Q", JsonSerializer.SerializeToElement("test") }
            });

        var response = await _factory.HttpClient.PostAsJsonAsync("/modules/Fake/stream?invalid=param", request,
            AmpelumHostContractsJsonContext.Default.ForwardRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task PromptsModule_NotFound_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.GetAsync("/modules/DoesNotExist/prompts", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PromptsModule_VersionCreate_BadJson_400_BadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new StringContent("{ invalid json", System.Text.Encoding.UTF8, "application/json");
        var response = await _factory.HttpClient.PostAsync("/prompts/Fake", content, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync(ct);
        var error = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        error.ShouldNotBeNull();
        error.Code.ShouldBe("bad_request");
    }

    [Fact]
    public async Task PromptsModule_Promote_NotFound_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _factory.HttpClient.PostAsync("/modules/Fake/prompts/promote?versionId=nonexistent", null, ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
