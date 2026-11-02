using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

public sealed class HostEndpointTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<ServiceFixture> StartAsync()
    {
        var service = new ServiceFixture();
        await service.InitializeAsync();
        return service;
    }

    [Fact]
    public async Task Forward_ReturnsTheModelAnswer_AndTheModelReceivedTheQuestion()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");

        var response = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        response.ModuleName.ShouldBe(AgentServiceHost.ModuleName);
        response.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        response.TraceId.ShouldMatch("^[0-9a-f]{32}$");
        service.Model.RecordedCalls.Count.ShouldBe(1);
        service.SentText(0).ShouldContain("How long is delivery?");
    }

    [Fact]
    public async Task Modules_ListsTheServedProgramWithItsFields()
    {
        await using var service = await StartAsync();

        var modules = await service.Client.ListModulesAsync(Ct);

        var module = modules.Modules.ShouldHaveSingleItem();
        module.Name.ShouldBe(AgentServiceHost.ModuleName);
        module.SignatureType.ShouldContain(nameof(AnswerSignature));
        module.Inputs.Select(f => f.Name).ShouldBe(["Question"]);
        module.Outputs.Select(f => f.Name).ShouldBe(["Answer"]);
    }

    [Fact]
    public async Task Forward_UnknownModule_Is404WithTheServicesErrorCode()
    {
        await using var service = await StartAsync();

        var failure = await Should.ThrowAsync<AgentServiceException>(
            () => service.Client.ForwardAsync("Missing", "anything", Ct));

        failure.Status.ShouldBe(HttpStatusCode.NotFound);
        failure.Error!.Code.ShouldBe(ErrorCodes.ModuleNotFound);
    }

    [Fact]
    public async Task Forward_MissingRequiredInput_Is422AndTheModelIsNeverCalled()
    {
        await using var service = await StartAsync();

        var failure = await Should.ThrowAsync<AgentServiceException>(
            () => service.Client.ForwardAsync(AgentServiceHost.ModuleName, new Dictionary<string, JsonElement>(), Ct));

        failure.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        failure.Error!.Code.ShouldBe(ErrorCodes.SignatureValidationFailed);
        failure.Error.Message.ShouldContain("Question");
        service.Model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Forward_MalformedJson_Is400()
    {
        await using var service = await StartAsync();
        using var body = new StringContent("{not json", Encoding.UTF8, "application/json");

        using var response = await service.Http.PostAsync($"/modules/{AgentServiceHost.ModuleName}/forward", body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Audit_RecordsEachCallAndTheChainVerifies()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("First answer.").EnqueueAnswer("Second answer.");

        var firstResponse = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "first question", Ct);
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "second question", Ct);

        var page = await service.Client.ListAuditAsync(Ct);
        page.Entries.Count.ShouldBe(2);
        page.Entries.Select(e => e.Inputs["Question"].GetString()).ShouldBe(["first question", "second question"]);
        page.Entries[1].PrevHashHex.ShouldBe(page.Entries[0].EntryHashHex);
        var first = page.Entries[0];
        first.ModuleName.ShouldBe("Predict");
        first.Outputs["Answer"].GetString().ShouldBe("First answer.");
        first.TraceId.ShouldBe(firstResponse.TraceId);
        first.EntryHashHex.ShouldNotBeNullOrEmpty();
        (await service.Client.VerifyAuditAsync(Ct)).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_IsRefusedWith422BecauseTheModuleIsNotMappedForStreaming()
    {
        await using var service = await StartAsync();
        using var body = new StringContent("""{"inputs":{"Question":"x"}}""", Encoding.UTF8, "application/json");

        using var response = await service.Http.PostAsync($"/modules/{AgentServiceHost.ModuleName}/stream", body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, Ct))!;
        error.Message.ShouldBe("Module does not support streaming.");
        service.Model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ready_IsOkWhenAModuleIsRegistered()
    {
        await using var service = await StartAsync();

        (await service.Client.ReadyStatusAsync(Ct)).ShouldBe(HttpStatusCode.OK);
    }
}
