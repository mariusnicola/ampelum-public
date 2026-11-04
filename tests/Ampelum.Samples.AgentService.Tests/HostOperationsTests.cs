using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>Prompt versions, audit by trace id, replay, optimize, health, admin and OpenAPI on the sample's Host.</summary>
public sealed class HostOperationsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<ServiceFixture> StartAsync()
    {
        var service = new ServiceFixture();
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
    public async Task PromptDemoExamples_AreStoredWithCamelCaseNames_AndReadBackWithTheirInputsAndOutputs()
    {
        await using var service = await StartAsync();

        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief."), Ct);

        var file = Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), "*.json", SearchOption.AllDirectories).Single();
        (await File.ReadAllTextAsync(file, Ct)).ShouldContain(
            "\"inputs\":{\"Question\":\"Where is my parcel?\"},\"outputs\":{\"Answer\":\"It is on its way.\"}");
        var listed = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Single().DemoAssignments["default"].Single();
        listed.Inputs["Question"].GetString().ShouldBe("Where is my parcel?");
        listed.Outputs["Answer"].GetString().ShouldBe("It is on its way.");
        version.DemoAssignments["default"].Single().Inputs["Question"].GetString().ShouldBe("Where is my parcel?");
        version.InstructionOverrides["default"].ShouldBe("Be brief.");
    }

    static CreatePromptVersionRequest WithDemo(string demoQuestion, string demoAnswer, IReadOnlyDictionary<string, string> instructions) =>
        new(
            new Dictionary<string, IReadOnlyList<DemoExample>>
            {
                ["default"] =
                [
                    new DemoExample(
                        new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement(demoQuestion) },
                        new Dictionary<string, JsonElement> { ["Answer"] = JsonSerializer.SerializeToElement(demoAnswer) }),
                ],
            },
            instructions);

    [Fact]
    public async Task Forward_SendsThePromotedVersionsDemonstrations_AndItsInstruction()
    {
        await using var service = await StartAsync();
        const string DemoQuestion = "DEMO-QUESTION-0b62";
        const string DemoAnswer = "DEMO-ANSWER-91ef";
        const string Instruction = "SENTINEL-WITH-DEMO-c3a8";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName,
            WithDemo(DemoQuestion, DemoAnswer, new Dictionary<string, string> { ["default"] = Instruction }), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("answer");

        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct);

        var sent = service.SentText(0);
        sent.ShouldContain(DemoQuestion);
        sent.ShouldContain(DemoAnswer);
        sent.ShouldContain(Instruction);
    }

    [Fact]
    public async Task Forward_AVersionFileWrittenWithPascalCaseDemonstrations_SendsItsInstructionAndItsDemonstration()
    {
        await using var service = await StartAsync();
        const string DemoQuestion = "OLD-FORMAT-DEMO-QUESTION-2c58";
        const string DemoAnswer = "OLD-FORMAT-DEMO-ANSWER-6f13";
        const string Instruction = "SENTINEL-OLD-FORMAT-e07b";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName,
            WithDemo(DemoQuestion, DemoAnswer, new Dictionary<string, string> { ["default"] = Instruction }), Ct);
        // The names an earlier Host wrote: the demonstration's fields were Pascal case.
        var file = Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), "*.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(file,
            (await File.ReadAllTextAsync(file, Ct)).Replace("\"inputs\"", "\"Inputs\"").Replace("\"outputs\"", "\"Outputs\""), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("answer");

        var listed = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Single().DemoAssignments["default"].Single();
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct);

        listed.Inputs["Question"].GetString().ShouldBe(DemoQuestion);
        listed.Outputs["Answer"].GetString().ShouldBe(DemoAnswer);
        service.SentText(0).ShouldContain(Instruction);
        service.SentText(0).ShouldContain(DemoQuestion);
        service.SentText(0).ShouldContain(DemoAnswer);
    }

    [Fact]
    public async Task Forward_AnExampleWithNeitherInputsNorOutputs_IsLeftOut_AndTheOthersAreSent()
    {
        await using var service = await StartAsync();
        const string DemoQuestion = "KEPT-DEMO-QUESTION-b9d4";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName,
            WithDemo(DemoQuestion, "kept demo answer", new Dictionary<string, string>()), Ct);
        var file = Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), "*.json", SearchOption.AllDirectories).Single();
        var text = await File.ReadAllTextAsync(file, Ct);
        await File.WriteAllTextAsync(file, text.Replace("\"default\":[", "\"default\":[{},"), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("answer");

        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct);

        service.SentText(0).ShouldContain(DemoQuestion);
    }

    [Fact]
    public async Task Forward_SendsAVersionsDemonstrations_EvenWhenItHasNoInstruction()
    {
        await using var service = await StartAsync();
        const string DemoQuestion = "DEMO-ONLY-QUESTION-5d17";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName,
            WithDemo(DemoQuestion, "demo only answer", new Dictionary<string, string>()), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("answer");

        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct);

        service.SentText(0).ShouldContain(DemoQuestion);
    }

    [Fact]
    public async Task Stream_SendsThePromotedVersionsDemonstrations()
    {
        await using var service = await StartAsync();
        const string DemoQuestion = "STREAM-DEMO-QUESTION-7a40";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName,
            WithDemo(DemoQuestion, "stream demo answer", new Dictionary<string, string> { ["default"] = "Be brief." }), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueStreamingAnswer("answer");

        await service.Client.StreamAsync(AgentServiceHost.ModuleName, "q", Ct);

        service.SentText(0).ShouldContain(DemoQuestion);
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
    public async Task Forward_SendsThePromotedInstruction_AndReportsTheVersionUsed()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-INSTRUCTION-7f3a";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");

        var response = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        response.PromptVersionId.ShouldBe(version.VersionId);
        service.SentText(0).ShouldContain(Sentinel);
    }

    [Fact]
    public async Task Stream_SendsThePromotedInstruction_AndTheFinalFrameNamesTheVersion()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-STREAM-4e9b";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        service.EnqueueStreamingAnswer("Delivery takes 3 to 5 business days.");

        var frames = await service.Client.StreamAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        service.SentText(0).ShouldContain(Sentinel);
        frames.Single(f => f.Event == "final").Data.GetProperty("promptVersionId").GetString().ShouldBe(version.VersionId);
    }

    [Fact]
    public async Task Forward_BeforeAnyPromotion_SendsTheModulesOwnInstruction_AndReportsNoVersion()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-INSTRUCTION-7f3a";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        service.EnqueueAnswer("first").EnqueueAnswer("second");

        var before = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        var after = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        before.PromptVersionId.ShouldBeNull();
        service.SentText(0).ShouldNotContain(Sentinel);
        after.PromptVersionId.ShouldBe(version.VersionId);
        service.SentText(1).ShouldContain(Sentinel);
    }

    [Fact]
    public async Task Forward_UsesTheVersionPromotedLast()
    {
        await using var service = await StartAsync();
        const string First = "SENTINEL-FIRST-91c2";
        const string Second = "SENTINEL-SECOND-5be8";
        var module = AgentServiceHost.ModuleName;
        var one = await service.Client.CreatePromptVersionAsync(module, Challenger(First), Ct);
        var two = await service.Client.CreatePromptVersionAsync(module, Challenger(Second, parent: one.VersionId), Ct);
        service.EnqueueAnswer("a").EnqueueAnswer("b");

        await service.Client.PromoteAsync(module, one.VersionId, Ct);
        var viaOne = await service.Client.ForwardAsync(module, "q", Ct);
        await service.Client.PromoteAsync(module, two.VersionId, Ct);
        var viaTwo = await service.Client.ForwardAsync(module, "q", Ct);

        viaOne.PromptVersionId.ShouldBe(one.VersionId);
        service.SentText(0).ShouldContain(First);
        viaTwo.PromptVersionId.ShouldBe(two.VersionId);
        service.SentText(1).ShouldContain(Second);
        service.SentText(1).ShouldNotContain(First);
    }

    static async Task<HttpResponseMessage> ForwardWithVersionAsync(ServiceFixture service, string versionId) =>
        await service.Http.PostAsJsonAsync(
            $"/modules/{AgentServiceHost.ModuleName}/forward",
            new ForwardRequest(
                new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement("q") }, versionId),
            AmpelumHostContractsJsonContext.Default.ForwardRequest, Ct);

    [Fact]
    public async Task Forward_WithAnExplicitVersionId_SendsThatVersionEvenWhenItIsNotPromoted()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-EXPLICIT-2d47";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        service.EnqueueAnswer("answer");

        using var response = await ForwardWithVersionAsync(service, version.VersionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ForwardResponse, Ct))!;
        body.PromptVersionId.ShouldBe(version.VersionId);
        service.SentText(0).ShouldContain(Sentinel);
    }

    [Fact]
    public async Task Forward_AfterAVersionedCall_ASecondCallWithoutAVersionSendsTheModulesOwnInstruction()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-ONCE-6a10";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        service.EnqueueAnswer("one").EnqueueAnswer("two");

        using var versioned = await ForwardWithVersionAsync(service, version.VersionId);
        var plain = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct);

        service.SentText(0).ShouldContain(Sentinel);
        plain.PromptVersionId.ShouldBeNull();
        service.SentText(1).ShouldNotContain(Sentinel);
    }

    [Fact]
    public async Task Forward_WhenThePromotedVersionsFileIsGone_Answers400ForwardCallFailed_AndCallsNoModel()
    {
        await using var service = await StartAsync();
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief."), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);
        File.Delete(Directory.GetFiles(Path.Combine(service.StorageDir, "prompts"), "*.json", SearchOption.AllDirectories).Single());

        var failure = await Should.ThrowAsync<AgentServiceException>(
            () => service.Client.ForwardAsync(AgentServiceHost.ModuleName, "q", Ct));

        failure.Status.ShouldBe(HttpStatusCode.BadRequest);
        failure.Error!.Message.ShouldStartWith("Forward call failed:");
        service.Model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Forward_WithAnUnknownVersionId_Answers404VersionNotFound_AndCallsNoModel()
    {
        await using var service = await StartAsync();

        using var response = await ForwardWithVersionAsync(service, "v-missing");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var error = (await response.Content.ReadFromJsonAsync(AmpelumHostContractsJsonContext.Default.ErrorResponse, Ct))!;
        error.Code.ShouldBe(ErrorCodes.VersionNotFound);
        service.Model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AuditByTrace_ReturnsTheRecordedCall_WithoutARebuild()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.").EnqueueAnswer("Second answer.");
        var first = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        var second = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "second question", Ct);

        var entry = await service.Client.GetAuditByTraceAsync(first.TraceId, Ct);

        entry.TraceId.ShouldBe(first.TraceId);
        entry.Inputs["Question"].GetString().ShouldBe("How long is delivery?");
        entry.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        (await service.Client.GetAuditByTraceAsync(second.TraceId, Ct)).Outputs["Answer"].GetString().ShouldBe("Second answer.");
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
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.RebuildIndexAsync(Ct);

        var replayed = await service.Client.ReplayAsync(original.TraceId, Ct);

        replayed.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        replayed.TraceId.ShouldBe(original.TraceId);
        service.Model.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Replay_ServesTheRecordedAnswer_WithoutARebuild()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);

        var replayed = await service.Client.ReplayAsync(original.TraceId, Ct);

        replayed.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        replayed.TraceId.ShouldBe(original.TraceId);
        service.Model.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Replay_OfAnUnknownTrace_Is404()
    {
        await using var service = await StartAsync();

        var failure = await Should.ThrowAsync<AgentServiceException>(() => service.Client.ReplayAsync(new string('b', 32), Ct));

        failure.Status.ShouldBe(HttpStatusCode.NotFound);
        failure.Error!.Code.ShouldBe(ErrorCodes.TraceNotFound);
    }

    [Fact]
    public async Task Replay_FindsTheModuleServedUnderAnotherName()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var original = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        await service.Client.RebuildIndexAsync(Ct);

        // The audit entry names the module "Predict", the registry knows it as "Answer".
        var replayed = await service.Client.ReplayAsync(original.TraceId, Ct);

        replayed.Outputs["Answer"].GetString().ShouldBe("Delivery takes 3 to 5 business days.");
        service.Model.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AuditFile_ReadsBackAsPlainValues()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        // A second handle on the live audit file, for reading only.
        await using var file = new SignalGrid.Ampelum.Audit.JsonFileAuditSink(Path.Combine(service.StorageDir, "audit.jsonl"));

        var entry = await file.EnumerateAsync(Ct).FirstAsync(Ct);

        entry.Inputs["Question"].ShouldBe("How long is delivery?");
        entry.Outputs["Answer"].ShouldBe("Delivery takes 3 to 5 business days.");
    }

    [Fact]
    public async Task Health_LivenessProbeAnswers200()
    {
        await using var service = await StartAsync();

        (await service.Client.HealthStatusAsync(Ct)).ShouldBe(HttpStatusCode.OK);
        var ready = await service.Client.WaitUntilReadyAsync(50, TimeSpan.FromMilliseconds(100), Ct);
        ready.Status.ShouldBe(HttpStatusCode.OK, ready.Body);
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
    public async Task RebuildIndex_KeepsPromptVersionsCreatedThroughTheEndpoint()
    {
        await using var service = await StartAsync();
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief."), Ct);
        (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Count.ShouldBe(1);

        var summary = await service.Client.RebuildIndexAsync(Ct);

        summary.PromptVersionsIndexed.ShouldBe(1);
        var listed = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.ShouldHaveSingleItem();
        listed.VersionId.ShouldBe(version.VersionId);
        listed.ModuleName.ShouldBe(AgentServiceHost.ModuleName);
        listed.InstructionOverrides["default"].ShouldBe("Be brief.");
        (await service.Client.ListPromptModulesAsync(Ct)).ModuleNames.ShouldBe([AgentServiceHost.ModuleName]);
    }

    [Fact]
    public async Task RebuildIndex_KeepsWhichPromptVersionWasPromoted_AndForwardStillUsesIt()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-REBUILD-2c8d";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);

        await service.Client.RebuildIndexAsync(Ct);

        (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions.Single().Status.ShouldBe(PromptStatus.Active);
        (await service.Client.GetActivePromptAsync(AgentServiceHost.ModuleName, Ct)).VersionId.ShouldBe(version.VersionId);
        service.EnqueueAnswer("Delivery takes 3 to 5 business days.");
        var response = await service.Client.ForwardAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        response.PromptVersionId.ShouldBe(version.VersionId);
        service.SentText(0).ShouldContain(Sentinel);
    }

    [Fact]
    public async Task RebuildIndex_KeepsWhichPromptVersionWasPromoted_AndStreamStillUsesIt()
    {
        await using var service = await StartAsync();
        const string Sentinel = "SENTINEL-REBUILD-STREAM-5a1f";
        var version = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger(Sentinel), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, version.VersionId, Ct);

        await service.Client.RebuildIndexAsync(Ct);

        service.EnqueueStreamingAnswer("Delivery takes 3 to 5 business days.");
        var frames = await service.Client.StreamAsync(AgentServiceHost.ModuleName, "How long is delivery?", Ct);
        service.SentText(0).ShouldContain(Sentinel);
        frames.Single(f => f.Event == "final").Data.GetProperty("promptVersionId").GetString().ShouldBe(version.VersionId);
    }

    [Fact]
    public async Task RebuildIndex_KeepsThePreviousActiveVersionRetired()
    {
        await using var service = await StartAsync();
        var first = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be brief.", note: "first"), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, first.VersionId, Ct);
        var second = await service.Client.CreatePromptVersionAsync(AgentServiceHost.ModuleName, Challenger("Be formal.", parent: first.VersionId), Ct);
        await service.Client.PromoteAsync(AgentServiceHost.ModuleName, second.VersionId, Ct);

        await service.Client.RebuildIndexAsync(Ct);

        var versions = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions;
        versions.Single(v => v.VersionId == first.VersionId).Status.ShouldBe(PromptStatus.Retired);
        versions.Single(v => v.VersionId == first.VersionId).Note.ShouldBe("first");
        versions.Single(v => v.VersionId == second.VersionId).Status.ShouldBe(PromptStatus.Active);
        versions.Single(v => v.VersionId == second.VersionId).ParentVersionId.ShouldBe(first.VersionId);
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
