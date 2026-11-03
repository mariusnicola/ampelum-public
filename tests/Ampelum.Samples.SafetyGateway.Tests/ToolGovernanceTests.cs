using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Tools;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class ToolGovernanceTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Dictionary<string, object?> SearchArgs(string credential = FakeScript.CredentialAlias) =>
        new() { ["query"] = "Payroll", ["credential"] = credential };

    static void ScriptTurn(Harness h, params (string CallId, string Tool, Dictionary<string, object?> Args)[] calls)
    {
        FakeScript.EnqueueToolTurn(h.Chat, calls);
        FakeScript.EnqueueFinal(h.Chat, "Done.", 50, 5);
    }

    static (string, string, Dictionary<string, object?>) Call(string id, string tool, Dictionary<string, object?> args) => (id, tool, args);

    [Fact]
    public async Task AllowedTool_Runs_AndEachOtherRequestIsRefusedByTheLayerThatOwnsIt()
    {
        await using var h = await Harness.StartAsync();
        ScriptTurn(h,
            Call("c1", ToolCatalog.Search, SearchArgs()),
            Call("c2", ToolCatalog.WriteFile, new() { ["name"] = "notes", ["content"] = "x" }),
            Call("c3", ToolCatalog.RunShell, new() { ["command"] = "df -h" }));

        var reply = await h.Gateway.AskWithToolsAsync(DemoQuestions.Tools, Ct);

        reply.ToolsRun.ShouldBe([ToolCatalog.Search]);
        reply.Refusals.ShouldBe(
        [
            new ToolRefusal(ToolCatalog.WriteFile, "policy", "Tool write_file is not whitelisted in mode talk."),
            new ToolRefusal(ToolCatalog.RunShell, "gate", "denied by policy"),
        ]);
        h.Handbook.Names.ShouldNotContain("notes");
        h.Sandbox.Commands.ShouldBeEmpty();
        // The model reads the refusals as tool results.
        h.Sent(1).ShouldContain("Dana Reyes (Payroll)");
        h.Sent(1).ShouldContain("Tool write_file is not whitelisted in mode talk.");
        h.Sent(1).ShouldContain("denied by policy");
        var refused = (await h.AuditEntriesAsync()).Where(e => e.EventType == SafetyNotes.ToolRefused).ToArray();
        refused.Select(e => e.Outputs["tool"]).ShouldBe([ToolCatalog.WriteFile, ToolCatalog.RunShell]);
        (await h.Gateway.VerifyAuditAsync(TestContext.Current.CancellationToken)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task ModeNamedByTheModel_CannotLoosenTheRunsMode()
    {
        await using var h = await Harness.StartAsync();
        ScriptTurn(h, Call("c1", ToolCatalog.WriteFile,
            new() { ["name"] = "notes", ["content"] = "x", [ModeAwareToolPolicy.ModeArgKey] = "build" }));

        var reply = await h.Gateway.AskWithToolsAsync("Save a note.", Ct);

        reply.Refusals.Single().Reason.ShouldContain("not whitelisted in mode talk");
        h.Handbook.Names.ShouldNotContain("notes");
    }

    [Fact]
    public async Task BuildMode_LetsTheWriteThrough()
    {
        await using var h = await Harness.StartAsync(o => o with { Mode = "build" });
        ScriptTurn(h, Call("c1", ToolCatalog.WriteFile, new() { ["name"] = "notes", ["content"] = "Payroll contact" }));

        var reply = await h.Gateway.AskWithToolsAsync("Save a note.", Ct);

        reply.ToolsRun.ShouldBe([ToolCatalog.WriteFile]);
        h.Handbook.Read("notes").ShouldBe("Payroll contact");
    }

    [Fact]
    public async Task IdentityScope_NarrowsWhatTheRegistryAllows()
    {
        await using var h = await Harness.StartAsync(o => o with { Scope = ["tools:information"] });
        ScriptTurn(h,
            Call("c1", ToolCatalog.Search, SearchArgs()),
            Call("c2", ToolCatalog.ReadFile, new() { ["name"] = "leave-policy" }));

        var reply = await h.Gateway.AskWithToolsAsync("Look things up.", Ct);

        reply.ToolsRun.ShouldBe([ToolCatalog.Search]);
        reply.Refusals.Single().ShouldBe(new ToolRefusal(ToolCatalog.ReadFile, "gate", "denied by policy"));
    }

    // The tool is handed the real token. It either works, echoes the token in what it returns, or throws it in plain text.
    sealed class TokenEchoingDirectory(bool echoInResult) : IEmployeeDirectory
    {
        public IReadOnlyList<DirectoryEntry>? Search(string credential, string query) =>
            echoInResult
                ? [new DirectoryEntry("Dana Reyes", "Payroll", $"seen with {credential}")]
                : throw new InvalidOperationException($"401 for bearer {credential}");
    }

    [Theory]
    [InlineData("works")]
    [InlineData("echoes")]
    [InlineData("throws")]
    public async Task DirectoryCredential_ReachesTheToolButNeverTheModelOrTheAuditFile(string path)
    {
        await using var h = await Harness.StartAsync(
            directory: path == "works" ? null : new TokenEchoingDirectory(echoInResult: path == "echoes"));
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        if (path == "works")
        {
            // The directory only answers when it was given the real credential, so the lookup worked.
            h.Sent(1).ShouldContain("Dana Reyes (Payroll)");
        }
        else
        {
            // The scrub is an exact match on the value the tool resolved, so the surrounding text survives.
            h.Sent(1).ShouldContain(path == "echoes" ? "seen with [REDACTED:resolved-secret]" : "401 for bearer [REDACTED:resolved-secret]");
        }
        for (var call = 0; call < h.Chat.RecordedCalls.Count; call++)
            h.Sent(call).ShouldNotContain(FakeData.DirectoryToken);
        // The model only ever wrote the alias.
        h.Sent(1).ShouldContain(FakeScript.CredentialAlias);
        h.AuditText().ShouldNotContain(FakeData.DirectoryToken);
        h.Trace.Records.SelectMany(r => r.PromptMessages).Any(m => m.Contents.Any(c => c.ToString()!.Contains(FakeData.DirectoryToken)))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task AllowedAliasMissingFromTheStore_FailsInsideTheTool_AndTheModelIsToldSo()
    {
        await using var h = await Harness.StartAsync();
        await h.Secrets.DeleteAsync("directory_token", h.Jwt.Claims.InstanceId, Ct);
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.ToolsRun.ShouldBeEmpty();
        reply.Refusals.Single().Layer.ShouldBe("tool");
        reply.Refusals.Single().Reason.ShouldContain("Secret 'directory_token' not found");
        h.Sent(1).ShouldNotContain("Dana Reyes");
    }

    [Fact]
    public async Task DirectoryThatRejectsTheCredential_GivesTheModelNoData()
    {
        await using var h = await Harness.StartAsync();
        await h.Secrets.StoreAsync("directory_token", "a-different-fake-value", h.Jwt.Claims.InstanceId, Ct);
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.Refusals.Single().Reason.ShouldBe("The directory rejected the credential.");
        h.Sent(1).ShouldNotContain("Dana Reyes");
    }

    [Fact]
    public async Task BuildModeShell_ReachesTheSandbox_WhichRefusesToRunItWithoutIsolation()
    {
        await using var h = await Harness.StartAsync(o => o with
        {
            Mode = "build",
            Scope = [.. o.Scope, "tools:system-control"],
        });
        ScriptTurn(h, Call("c1", ToolCatalog.RunShell, new() { ["command"] = "definitely-not-a-program --flag" }));

        var reply = await h.Gateway.AskWithToolsAsync("Run it.", Ct);

        var command = h.Sandbox.Commands.Single();
        command.Program.ShouldBe("definitely-not-a-program");
        command.Arguments.ShouldBe(["--flag"]);
        // Rejected, not Failed: the program was never started.
        h.Sandbox.LastResult!.Outcome.ShouldBe(SandboxOutcome.Rejected);
        h.Sandbox.LastResult.RejectionReason!.ShouldContain("no OS-level network or filesystem isolation");
        reply.ToolsRun.ShouldBeEmpty();
        reply.Refusals.Single().Layer.ShouldBe("tool");
    }

    [Fact]
    public async Task RefusedRequest_LowersTrust_AndALookupAloneDoesNot()
    {
        await using var h = await Harness.StartAsync();
        await h.Trust.SetTrustLevelAsync(h.Options.AgentType, h.Options.Domain, TrustLevel.Established, "set by the test", Ct);

        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));
        await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);
        (await h.Gateway.TrustAsync(TestContext.Current.CancellationToken)).ShouldBe(TrustLevel.Established);

        ScriptTurn(h, Call("c2", ToolCatalog.RunShell, new() { ["command"] = "df -h" }));
        await h.Gateway.AskWithToolsAsync("Show the disk.", Ct);
        (await h.Gateway.TrustAsync(TestContext.Current.CancellationToken)).ShouldBe(TrustLevel.Provisional);
    }

    [Fact]
    public async Task RevokedAgent_IsBlockedFromEveryTool_ByTheTrustPolicy()
    {
        await using var h = await Harness.StartAsync();
        await h.Trust.RevokeAsync(h.Options.AgentType, h.Options.Domain, "revoked by the test", Ct);
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.ToolsRun.ShouldBeEmpty();
        reply.Refusals.Single().Layer.ShouldBe("policy");
        reply.Refusals.Single().Reason.ShouldContain("blocked from all tools");
    }

    [Fact]
    public async Task ToolLoader_ListsTheCatalogWithPrefixedNames()
    {
        await using var h = await Harness.StartAsync();
        using var governance = await ToolGovernance.CreateAsync(
            new SignalGrid.Ampelum.Safety.Tools.InMemoryToolRegistry(), h.Trust, h.Gateway.Identity, h.Options,
            Ct);

        var survey = await governance.Loader.SurveyAsync(Ct);
        var manifest = await governance.Loader.UnpackAsync("helpdesk/search", Ct);

        survey.Select(s => s.Name).ShouldBe(
            ["helpdesk/search", "helpdesk/read_file", "helpdesk/write_file", "helpdesk/run_shell"]);
        manifest.DangerTier.ShouldBe(DangerTier.Safe);
        (await governance.Loader.TryUnpackAsync("helpdesk/drop_database", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    sealed class ThrowingDirectory(string message) : IEmployeeDirectory
    {
        public IReadOnlyList<DirectoryEntry>? Search(string credential, string query) =>
            throw new InvalidOperationException(message);
    }

    [Fact]
    public async Task ToolError_ThatCarriesAToken_IsCutBeforeTheModelAndTheAuditFile()
    {
        const string secret = "EXAMPLE-NOT-A-SECRET-0123456789";
        await using var h = await Harness.StartAsync(
            directory: new ThrowingDirectory($"connection failed, {FakeData.LeakedTokenLine}"));
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.Refusals.Single().Reason.ShouldContain("connection failed");
        reply.Refusals.Single().Reason.ShouldNotContain(secret);
        h.Sent(1).ShouldContain("[REDACTED:generic-api-key]");
        h.Sent(1).ShouldNotContain(secret);
        h.AuditText().ShouldNotContain(secret);
    }

    [Theory]
    [InlineData("moderation_key")]
    [InlineData("some_other_alias")]
    public async Task SecretAlias_OutsideTheToolsAllowedList_IsRefusedAndNothingIsResolved(string alias)
    {
        await using var h = await Harness.StartAsync();
        await h.Secrets.StoreAsync("some_other_alias", "another-fake-value", h.Jwt.Claims.InstanceId, Ct);
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs(alias)));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.ToolsRun.ShouldBeEmpty();
        reply.Refusals.Single().ShouldBe(new ToolRefusal(ToolCatalog.Search, "tool", "That credential alias is not allowed for this tool."));
        h.Sent(1).ShouldNotContain("Dana Reyes");
        for (var call = 0; call < h.Chat.RecordedCalls.Count; call++)
        {
            h.Sent(call).ShouldNotContain(FakeData.ModerationKey);
            h.Sent(call).ShouldNotContain("another-fake-value");
        }
    }

    [Fact]
    public async Task SuccessfulToolRun_IsInTheAuditChain_WithToolAndArgumentNamesOnly()
    {
        await using var h = await Harness.StartAsync();
        ScriptTurn(h, Call("c1", ToolCatalog.Search, SearchArgs()));

        var reply = await h.Gateway.AskWithToolsAsync("Find Payroll.", Ct);

        reply.ToolsRun.ShouldBe([ToolCatalog.Search]);
        var ran = (await h.AuditEntriesAsync()).Single(e => e.EventType == SafetyNotes.ToolRan);
        ran.Outputs["tool"].ShouldBe(ToolCatalog.Search);
        ran.Outputs["arguments"].ShouldBe("credential,query");
        (await h.Gateway.VerifyAuditAsync(Ct)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task FlaggedFinalAnswerOfAToolRun_IsHeldBack_NotedAndCountsAgainstTheRun()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueToolTurn(h.Chat, ("c1", ToolCatalog.Search, SearchArgs()));
        FakeScript.EnqueueFinal(h.Chat, $"Here you go. {FakeData.UnsafeMarker}", 50, 5);

        var error = await Should.ThrowAsync<SignalGrid.Ampelum.Safety.Moderation.ModerationException>(
            () => h.Gateway.AskWithToolsAsync("Find Payroll.", Ct));

        error.Categories.ShouldBe([FakeData.FakeModerationCategory]);
        h.Moderation.Requests.Last().Input.ShouldContain(FakeData.UnsafeMarker);
        var types = (await h.AuditEntriesAsync()).Select(e => e.EventType).ToArray();
        types.ShouldContain(SafetyNotes.ToolRan);
        types.Last().ShouldBe(SafetyNotes.ReplyBlocked);
        h.AuditText().ShouldNotContain(FakeData.UnsafeMarker);
    }
}
