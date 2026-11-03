using System.Text.Json;
using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class IdentityAndSecretsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TokenWithABrokenSignature_IsRefusedAtStart()
    {
        await using var h = await Harness.StartAsync();
        var parts = h.Jwt.Token.Split('.');
        // Change one character in the middle of the signature, where every bit counts.
        parts[2] = parts[2][..10] + (parts[2][10] == 'A' ? 'B' : 'A') + parts[2][11..];
        var counterfeit = new AgentJwt(string.Join('.', parts), h.Jwt.Claims);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => SafetyGateway.StartAsync(h.Dependencies(), counterfeit, Ct));
    }

    [Fact]
    public async Task ExpiredToken_IsRefusedAtStart()
    {
        // A negative lifetime puts the expiry in the past, so the test does not wait for anything.
        await using var h = await Harness.StartAsync(lifetime: TimeSpan.FromMinutes(-1), start: false);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => SafetyGateway.StartAsync(h.Dependencies(), h.Jwt, Ct));
    }

    [Fact]
    public async Task TokenRevokedWhileRunning_StopsTheNextQuestionBeforeTheModelIsCalled()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "One.", 10, 1);
        await h.Gateway.AskAsync("First?", Ct);

        h.Identity.Revoked = true;

        await Should.ThrowAsync<UnauthorizedAccessException>(() => h.Gateway.AskAsync("Second?", Ct));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => h.Gateway.AskWithToolsAsync("Third?", Ct));
        h.Chat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EveryAuditEntry_NamesTheRunThatMadeIt()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "One.", 10, 1);
        FakeScript.EnqueueAnswer(h.Chat, "Two.", 10, 1);

        await h.Gateway.AskAsync("First?", Ct);
        await h.Gateway.AskAsync("Second?", Ct);

        var entries = await h.AuditEntriesAsync();
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.TraceId == h.Jwt.Claims.InstanceId);
        h.Gateway.Identity.AgentType.ShouldBe("helpdesk-assistant");
        h.Gateway.Identity.Scope.ShouldBe(h.Options.Scope);
    }

    [Fact]
    public async Task MissingModerationSecret_StopsTheGatewayBeforeAnyQuestion()
    {
        await using var h = await Harness.StartAsync();
        var deps = h.Dependencies() with { Secrets = new InMemorySecretStore() };

        var error = await Should.ThrowAsync<SecretNotFoundException>(() => SafetyGateway.StartAsync(deps, h.Jwt, Ct));

        error.Alias.ShouldBe(h.Options.ModerationKeyAlias);
        h.Chat.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Secrets_BelongToOneRun_ANewInstanceDoesNotSeeThem()
    {
        await using var h = await Harness.StartAsync();
        var secondRun = await h.Identity.IssueAsync(h.Options.AgentType, [.. h.Options.Scope], TimeSpan.FromMinutes(5), Ct);

        await Should.ThrowAsync<SecretNotFoundException>(() => SafetyGateway.StartAsync(h.Dependencies(), secondRun, Ct));

        (await h.Secrets.ExistsAsync(h.Options.ModerationKeyAlias, h.Jwt.Claims.InstanceId, Ct)).ShouldBeTrue();
        (await h.Secrets.ExistsAsync(h.Options.ModerationKeyAlias, secondRun.Claims.InstanceId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task SecretRefResolver_SwapsTheAliasForTheValue_AndClearsItAfterUse()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync("directory_token", FakeData.DirectoryToken, "run-1", Ct);
        var schema = ToolCatalog.All.First(t => t.Manifest.Name == ToolCatalog.Search).Manifest.ArgsSchema;
        var fields = SecretRefResolver.ExtractSecretRefFields(schema);
        var args = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("Payroll"),
            ["credential"] = JsonSerializer.SerializeToElement("directory_token"),
        };

        var resolved = await new SecretRefResolver(store).ResolveSecretsAsync(fields, args, "run-1", Ct);

        fields.ShouldBe(["credential"]);
        resolved["credential"].GetString().ShouldBe(FakeData.DirectoryToken);
        resolved["query"].GetString().ShouldBe("Payroll");
        args["credential"].GetString().ShouldBe("directory_token");
        SecretRefResolver.ClearResolvedSecrets(fields, resolved);
        resolved["credential"].GetString().ShouldBeEmpty();
    }

    [Fact]
    public async Task SafetyNotes_CarryTheRunsTraceIdLikeModelEntries()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueToolTurn(h.Chat, ("c1", ToolCatalog.RunShell, new Dictionary<string, object?> { ["command"] = "df -h" }));
        FakeScript.EnqueueFinal(h.Chat, "Done.", 10, 1);

        await h.Gateway.AskWithToolsAsync("Show the disk.", Ct);

        var entries = await h.AuditEntriesAsync();
        entries.ShouldContain(e => e.EventType == SafetyNotes.ToolRefused);
        entries.ShouldContain(e => e.EventType == "forward.completed");
        entries.ShouldAllBe(e => e.TraceId == h.Jwt.Claims.InstanceId);
    }
}
