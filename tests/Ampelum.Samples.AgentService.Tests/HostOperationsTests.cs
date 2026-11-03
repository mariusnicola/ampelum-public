using System.Net;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>Prompt versions, audit by trace id, replay, optimize, health, admin and OpenAPI on the sample's Host.</summary>
public sealed class HostOperationsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<ServiceFixture> StartAsync(bool replayModule = false)
    {
        var service = new ServiceFixture(replayModule);
        await service.InitializeAsync();
        return service;
    }

    static CreatePromptVersionRequest Challenger(string instruction, string? parent = null, string? note = null) =>
        new(
            new Dictionary<string, IReadOnlyList<DemoExample>>
            {
                ["default"] =
                [
                    new DemoExample(
                        new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement("Where is my parcel?") },
                        new Dictionary<string, JsonElement> { ["Answer"] = JsonSerializer.SerializeToElement("It is on its way.") }),
                ],
            },
            new Dictionary<string, string> { ["default"] = instruction },
            parent,
            note);

    [Fact]
    public async Task Prompts_CreateListPromoteAndActive_FollowTheVersionThroughItsStatuses()
    {
        await using var service = await StartAsync();
        var module = AgentServiceHost.ModuleName;

        (await service.Client.ListPromptModulesAsync(Ct)).ModuleNames.ShouldBeEmpty();
        var noActive = await Should.ThrowAsync<AgentServiceException>(() => service.Client.GetActivePromptAsync(module, Ct));
        noActive.Status.ShouldBe(HttpStatusCode.NotFound);
        noActive.Error!.Code.ShouldBe(ErrorCodes.NotFound);

        var first = await service.Client.CreatePromptVersionAsync(module, Challenger("Be brief.", note: "first"), Ct);
        first.Status.ShouldBe(PromptStatus.Challenger);
        first.ModuleName.ShouldBe(module);
        first.Note.ShouldBe("first");
        first.InstructionOverrides["default"].ShouldBe("Be brief.");
        first.DemoAssignments["default"].Count.ShouldBe(1);

        (await service.Client.ListPromptModulesAsync(Ct)).ModuleNames.ShouldBe([module]);
        (await service.Client.ListPromptVersionsAsync(module, Ct)).Versions.Select(v => v.VersionId).ShouldBe([first.VersionId]);

        var promoted = await service.Client.PromoteAsync(module, first.VersionId, Ct);
        promoted.ActiveVersionId.ShouldBe(first.VersionId);
        promoted.PreviousActiveVersionId.ShouldBeNull();
        var active = await service.Client.GetActivePromptAsync(module, Ct);
        active.VersionId.ShouldBe(first.VersionId);
        active.Status.ShouldBe(PromptStatus.Active);
        active.InstructionOverrides["default"].ShouldBe("Be brief.");

        var second = await service.Client.CreatePromptVersionAsync(module, Challenger("Be formal.", parent: first.VersionId), Ct);
        second.ParentVersionId.ShouldBe(first.VersionId);
        var replaced = await service.Client.PromoteAsync(module, second.VersionId, Ct);
        replaced.PreviousActiveVersionId.ShouldBe(first.VersionId);
        var statuses = (await service.Client.ListPromptVersionsAsync(module, Ct)).Versions.ToDictionary(v => v.VersionId, v => v.Status);
        statuses[first.VersionId].ShouldBe(PromptStatus.Retired);
        statuses[second.VersionId].ShouldBe(PromptStatus.Active);
    }

    [Fact]
    public async Task KnownLimit_PromptDemoExamplesAreStoredButReadBackWithoutTheirFields()
    {
        await using var service = await StartAsync();

        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief."), Ct);

        // The file holds the example: it is written with the serializer's default (Pascal case) names.
        var file = Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), "*.json", SearchOption.AllDirectories).Single();
        (await File.ReadAllTextAsync(file, Ct)).ShouldContain("Where is my parcel?");
        // The read side expects camel case, so the example comes back with no inputs and no outputs.
        var demo = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Single().DemoAssignments["default"].Single();
        demo.Inputs.ShouldBeNull();
        demo.Outputs.ShouldBeNull();
        version.InstructionOverrides["default"].ShouldBe("Be brief.");
    }

    [Fact]
    public async Task Prompts_PromoteRefusesAnUnknownVersionAndAnAlreadyActiveOne()
    {
        await using var service = await StartAsync();
        var module = AgentServiceHost.ModuleName;
        var version = await service.Client.CreatePromptVersionAsync(module, Challenger("Be brief."), Ct);
        await service.Client.PromoteAsync(module, version.VersionId, Ct);

        var unknown = await Should.ThrowAsync<AgentServiceException>(() => service.Client.PromoteAsync(module, "v-missing", Ct));
        var again = await Should.ThrowAsync<AgentServiceException>(() => service.Client.PromoteAsync(module, version.VersionId, Ct));
        var wrongModule = await Should.ThrowAsync<AgentServiceException>(
            () => service.Client.CreatePromptVersionAsync("Missing", Challenger("x"), Ct));

        unknown.Error!.Code.ShouldBe(ErrorCodes.VersionNotFound);
        again.Status.ShouldBe(HttpStatusCode.Conflict);
        again.Error!.Code.ShouldBe(ErrorCodes.PromoteConflict);
        wrongModule.Error!.Code.ShouldBe(ErrorCodes.ModuleNotFound);
    }

    [Fact]
    public async Task KnownLimit_PromotedPromptIsNotAppliedToForward()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-INSTRUCTION-7f3a";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");

        var response = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        // The forward endpoint only echoes a version id the caller sends; it never loads the active version.
        response.PromptVersionId.ShouldBeNull();
        service.SentText(0).ShouldNotContain(Sentinel);
    }

    [Fact]
    public async Task KnownLimit_AuditByTraceIdNeedsARebuildBecauseForwardDoesNotIndex()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var response = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        var failure = await Should.ThrowAsync<AgentServiceException>(() => service.Client.GetAuditByTraceAsync(response.TraceId, Ct));

        failure.Status.ShouldBe(HttpStatusCode.NotFound);
        failure.Error!.Code.ShouldBe(ErrorCodes.TraceNotFound);
        (await service.Client.ListAuditAsync(Ct)).Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AuditByTrace_ReturnsTheRecordedCall_OnceTheIndexIsRebuilt()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("First answer.").EnqueueAnswer("Second answer.");
        var first = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "first question", Ct);
        var second = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "second question", Ct);

        var summary = await service.Client.RebuildIndexAsync(Ct);

        summary.AuditEntriesIndexed.ShouldBe(2);
        var entry = await service.Client.GetAuditByTraceAsync(second.TraceId, Ct);
        entry.TraceId.ShouldBe(second.TraceId);
        entry.Inputs["Question"].GetString().ShouldBe("second question");
        entry.Outputs["Answer"].GetString().ShouldBe("Second answer.");
        (await service.Client.GetAuditByTraceAsync(first.TraceId, Ct)).Outputs["Answer"].GetString().ShouldBe("First answer.");
    }

    [Fact]
    public async Task AuditByTrace_RejectsAMalformedIdAndAnUnknownOne()
    {
        await using var service = await StartAsync();

        var malformed = await Should.ThrowAsync<AgentServiceException>(() => service.Client.GetAuditByTraceAsync("not-a-trace-id", Ct));
        var unknown = await Should.ThrowAsync<AgentServiceException>(
            () => service.Client.GetAuditByTraceAsync(new string('a', 32), Ct));

        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.Error!.Code.ShouldBe(ErrorCodes.TraceNotFound);
    }

    [Fact]
    public async Task Replay_ServesTheRecordedAnswerWithoutCallingTheModel()
    {
        await using var service = await StartAsync(replayModule: true);
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.RebuildIndexAsync(Ct);

        var replayed = await service.Client.ReplayAsync(original.TraceId, Ct);

        replayed.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        replayed.TraceId.ShouldBe(original.TraceId);
        service.Model.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Replay_OfAnUnknownTrace_Is404()
    {
        await using var service = await StartAsync(replayModule: true);

        var failure = await Should.ThrowAsync<AgentServiceException>(() => service.Client.ReplayAsync(new string('b', 32), Ct));

        failure.Status.ShouldBe(HttpStatusCode.NotFound);
        failure.Error!.Code.ShouldBe(ErrorCodes.TraceNotFound);
    }

    [Fact]
    public async Task KnownLimit_ReplayFindsNothingWhenOnlyTheServedNameIsRegistered()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.RebuildIndexAsync(Ct);

        var failure = await Should.ThrowAsync<AgentServiceException>(() => service.Client.ReplayAsync(original.TraceId, Ct));

        // The audit entry names the module "Predict", the registry knows it as "Answer".
        failure.Status.ShouldBe(HttpStatusCode.NotFound);
        failure.Error!.Code.ShouldBe(ErrorCodes.ModuleNotFound);
        failure.Error.Message.ShouldContain(AgentServiceHost.ReplayModuleName);
    }

    /// <summary>A Host with one audited call; the two sinks below open its live audit file for reading only.</summary>
    async Task<(ServiceFixture Service, SignalGrid.Ampelum.Audit.JsonFileAuditSink Library, PlainValueAuditSink Sample)> AuditedCallAsync()
    {
        var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        var auditFile = Path.Combine(service.StorageDir, "audit.jsonl");
        return (service, new SignalGrid.Ampelum.Audit.JsonFileAuditSink(auditFile), new PlainValueAuditSink(auditFile));
    }

    [Fact]
    public async Task KnownLimit_JsonFileAuditSinkReadsValuesBackAsJsonElement()
    {
        var (service, library, sample) = await AuditedCallAsync();
        await using var _ = service;
        await using var __ = library;
        await using var ___ = sample;

        var fromLibrary = await library.EnumerateAsync(Ct).FirstAsync(Ct);
        var fromSample = await sample.EnumerateAsync(Ct).FirstAsync(Ct);

        fromLibrary.Inputs["Question"].ShouldBeOfType<JsonElement>();
        fromSample.Inputs["Question"].ShouldBe("How long is delivery?");
        fromSample.Outputs["Answer"].ShouldBe("Delivery takes 3 to 5 business days.");
        fromSample.EntryHash.ShouldBe(fromLibrary.EntryHash);
    }

    [Fact]
    public async Task KnownLimit_VerifyThrowsWhenTheHostUsesTheLibraryFileSink()
    {
        await using var service = new ServiceFixture(libraryAuditSink: true);
        await service.InitializeAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        // The test server hands the endpoint's exception to the caller; a real server would answer 500.
        var failure = await Should.ThrowAsync<SignalGrid.Ampelum.Audit.AuditSerializationException>(
            () => service.Client.VerifyAuditAsync(Ct));

        failure.Message.ShouldContain(nameof(JsonElement));
    }

    [Fact]
    public async Task KnownLimit_ReplayThrowsWhenTheHostUsesTheLibraryFileSink()
    {
        await using var service = new ServiceFixture(replayModule: true, libraryAuditSink: true);
        await service.InitializeAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.RebuildIndexAsync(Ct);

        var failure = await Should.ThrowAsync<SignalGrid.Ampelum.Audit.AuditSerializationException>(
            () => service.Client.ReplayAsync(original.TraceId, Ct));

        failure.Message.ShouldContain(nameof(JsonElement));
    }

    [Fact]
    public async Task Health_LivenessProbeAnswers200()
    {
        await using var service = await StartAsync();

        (await service.Client.HealthStatusAsync(Ct)).ShouldBe(HttpStatusCode.OK);
        (await service.Client.ReadyStatusAsync(Ct)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_RebuildIndex_ReportsWhatItIndexed()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("First answer.").EnqueueAnswer("Second answer.");
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "first question", Ct);
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "second question", Ct);

        var summary = await service.Client.RebuildIndexAsync(Ct);

        summary.AuditEntriesIndexed.ShouldBe(2);
        summary.ModulesSnapshot.ShouldBe(1);
        summary.PromptVersionsIndexed.ShouldBe(0);
        summary.OptimizerRunsCleared.ShouldBe(0);
    }

    [Fact]
    public async Task KnownLimit_RebuildIndexForgetsPromptVersionsCreatedThroughTheEndpoint()
    {
        await using var service = await StartAsync();
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief."), Ct);
        (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Count.ShouldBe(1);

        var summary = await service.Client.RebuildIndexAsync(Ct);

        // The rebuild empties the prompt table and refills it from the top of the prompt folder only,
        // while the endpoint writes each file into a folder named after the module.
        summary.PromptVersionsIndexed.ShouldBe(0);
        (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), $"{version.VersionId}.json", SearchOption.AllDirectories)
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OpenApi_DocumentListsTheHostsRoutes()
    {
        await using var service = await StartAsync();

        var document = await service.Client.GetOpenApiAsync(Ct);

        document.GetProperty("openapi").GetString().ShouldNotBeNullOrEmpty();
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToArray();
        paths.ShouldContain("/modules/{moduleName}/forward");
        paths.ShouldContain("/audit/{traceId}");
        paths.ShouldContain("/audit/replay/{traceId}");
        paths.ShouldContain("/prompts/{moduleName}/promote");
        paths.ShouldContain("/optimize/runs/{runId}/stream");
        paths.ShouldContain("/admin/rebuild-index");
        paths.ShouldContain("/readyz");
    }
}
