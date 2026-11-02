#pragma warning disable AMPELUM_V09_PREVIEW
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Endpoints;

public sealed class PromptsEndpointTests : IAsyncLifetime
{
    readonly ModuleTestFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GET_prompts_EmptyStore_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _factory.HttpClient.GetAsync("/prompts", ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<PromptIndexResponse>(json, AmpelumHostContractsJsonContext.Default.PromptIndexResponse);
        body.ShouldNotBeNull();
        body.ModuleNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task POST_prompts_create_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreatePromptVersionRequest(
            DemoAssignments: new Dictionary<string, IReadOnlyList<DemoExample>>(),
            InstructionOverrides: new Dictionary<string, string>());

        var response = await _factory.HttpClient.PostAsJsonAsync("/prompts/DoesNotExist", request,
            AmpelumHostContractsJsonContext.Default.CreatePromptVersionRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
    }

    [Fact]
    public async Task POST_prompts_promote_404_UnknownModule()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new PromoteRequest("v-some-id");

        var response = await _factory.HttpClient.PostAsJsonAsync("/prompts/DoesNotExist/promote", request,
            AmpelumHostContractsJsonContext.Default.PromoteRequest, cancellationToken: ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync(ct);
        var body = JsonSerializer.Deserialize<ErrorResponse>(json, AmpelumHostContractsJsonContext.Default.ErrorResponse);
        body.ShouldNotBeNull();
        body.Code.ShouldBe("module_not_found");
    }
}
