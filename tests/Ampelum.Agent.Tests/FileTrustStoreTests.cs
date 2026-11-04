using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class FileTrustStoreTests
{
    [Fact]
    public async Task GetTrustLevelAsync_NewAgent_ReturnsUnknown()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var level = await store.GetTrustLevelAsync("new-agent", "ampelum");

        level.ShouldBe(TrustLevel.Unknown);
    }

    [Fact]
    public async Task SetTrustLevelAsync_AdminOverride_SetsLevel()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        await store.SetTrustLevelAsync("agent-1", "ampelum", TrustLevel.Provisional, "admin grant");

        var level = await store.GetTrustLevelAsync("agent-1", "ampelum");
        level.ShouldBe(TrustLevel.Provisional);
    }

    [Fact]
    public async Task RecordOutcomeAsync_SecurityViolation_DemotesTrust()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Set initial trust level to Provisional
        await store.SetTrustLevelAsync("agent-2", "ampelum", TrustLevel.Provisional, "initial");

        // Record a security violation
        var outcome = new TrustOutcome(
            RunInstanceId: "run-1",
            Succeeded: false,
            HadSecurityViolation: true,
            HadEscalation: false,
            ToolCallCount: 5,
            OccurredAt: DateTimeOffset.UtcNow
        );

        await store.RecordOutcomeAsync("agent-2", "ampelum", outcome);

        // Trust should be demoted to Unknown
        var level = await store.GetTrustLevelAsync("agent-2", "ampelum");
        level.ShouldBe(TrustLevel.Unknown);
    }

    [Fact]
    public async Task RecordOutcomeAsync_ViolationFromUnknown_StaysUnknown()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // New agent at Unknown level
        var outcome = new TrustOutcome(
            RunInstanceId: "run-2",
            Succeeded: false,
            HadSecurityViolation: true,
            HadEscalation: false,
            ToolCallCount: 3,
            OccurredAt: DateTimeOffset.UtcNow
        );

        await store.RecordOutcomeAsync("agent-3", "ampelum", outcome);

        // Trust should remain at Unknown (minimum)
        var level = await store.GetTrustLevelAsync("agent-3", "ampelum");
        level.ShouldBe(TrustLevel.Unknown);
    }

    [Fact]
    public async Task RevokeAsync_DropsToNone()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        // Set initial trust
        await store.SetTrustLevelAsync("agent-4", "ampelum", TrustLevel.Established, "initial");

        // Revoke
        await store.RevokeAsync("agent-4", "ampelum", "security incident");

        // Trust should be None
        var level = await store.GetTrustLevelAsync("agent-4", "ampelum");
        level.ShouldBe(TrustLevel.None);
    }

    [Fact]
    public async Task DifferentDomains_IndependentTrust()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        await store.SetTrustLevelAsync("agent-5", "ampelum", TrustLevel.Trusted, "ampelum access");
        await store.SetTrustLevelAsync("agent-5", "artisan.email", TrustLevel.Provisional, "email access");

        var ampelumLevel = await store.GetTrustLevelAsync("agent-5", "ampelum");
        var emailLevel = await store.GetTrustLevelAsync("agent-5", "artisan.email");

        ampelumLevel.ShouldBe(TrustLevel.Trusted);
        emailLevel.ShouldBe(TrustLevel.Provisional);
    }

    [Fact]
    public async Task SetTrustLevelAsync_NullAgentType_Throws()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        await Should.ThrowAsync<ArgumentNullException>(
            () => store.SetTrustLevelAsync(null!, "ampelum", TrustLevel.Unknown, "reason"));
    }

    [Fact]
    public async Task RevokeAsync_NullReason_Throws()
    {
        var store = new FileTrustStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        await Should.ThrowAsync<ArgumentNullException>(
            () => store.RevokeAsync("agent", "domain", null!));
    }
}

public sealed class FileTrustStorePersistenceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static TrustOutcome Violation() => new("run", false, true, false, 1, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SetTrustLevel_SurvivesANewStoreOnTheSameFolder()
    {
        await new FileTrustStore(_dir).SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);

        (await new FileTrustStore(_dir).GetTrustLevelAsync("agent", "mail", Ct)).ShouldBe(TrustLevel.Trusted);
    }

    [Fact]
    public async Task EachAgentTypeAndDomainPair_HasItsOwnLevel_AfterARestart()
    {
        var first = new FileTrustStore(_dir);
        await first.SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        await first.SetTrustLevelAsync("agent", "files", TrustLevel.Provisional, "grant", Ct);
        await first.SetTrustLevelAsync("other", "mail", TrustLevel.Established, "grant", Ct);

        var second = new FileTrustStore(_dir);

        (await second.GetTrustLevelAsync("agent", "mail", Ct)).ShouldBe(TrustLevel.Trusted);
        (await second.GetTrustLevelAsync("agent", "files", Ct)).ShouldBe(TrustLevel.Provisional);
        (await second.GetTrustLevelAsync("other", "mail", Ct)).ShouldBe(TrustLevel.Established);
        (await second.GetTrustLevelAsync("other", "files", Ct)).ShouldBe(TrustLevel.Unknown);
        Directory.GetFiles(_dir).Length.ShouldBe(3);
    }

    [Fact]
    public async Task ViolationDemotion_IsPersisted_ByOneLevel()
    {
        var first = new FileTrustStore(_dir);
        await first.SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        await first.RecordOutcomeAsync("agent", "mail", Violation(), Ct);

        (await new FileTrustStore(_dir).GetTrustLevelAsync("agent", "mail", Ct)).ShouldBe(TrustLevel.Trusted - 1);
    }

    [Fact]
    public async Task ViolationAfterARestart_DemotesTheStoredLevel()
    {
        await new FileTrustStore(_dir).SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        var second = new FileTrustStore(_dir);

        await second.RecordOutcomeAsync("agent", "mail", Violation(), Ct);

        (await second.GetTrustLevelAsync("agent", "mail", Ct)).ShouldBe(TrustLevel.Trusted - 1);
    }

    [Fact]
    public async Task Revoke_IsPersisted()
    {
        var first = new FileTrustStore(_dir);
        await first.SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        await first.RevokeAsync("agent", "mail", "incident", Ct);

        (await new FileTrustStore(_dir).GetTrustLevelAsync("agent", "mail", Ct)).ShouldBe(TrustLevel.None);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("Superuser")]
    [InlineData("provisional")]
    [InlineData("")]
    public async Task LevelInTheFileThatIsNotADefinedName_ThrowsInvalidDataExceptionNamingTheFile(string level)
    {
        await new FileTrustStore(_dir).SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        var file = Directory.GetFiles(_dir).Single();
        File.WriteAllText(file, $$"""{ "agentType": "agent", "domain": "mail", "level": "{{level}}" }""");

        var ex = await Should.ThrowAsync<InvalidDataException>(() => new FileTrustStore(_dir).GetTrustLevelAsync("agent", "mail", Ct));

        ex.Message.ShouldContain(Path.GetFileName(file));
    }

    [Fact]
    public async Task FileWithoutALevel_ThrowsInvalidDataExceptionNamingTheFile()
    {
        await new FileTrustStore(_dir).SetTrustLevelAsync("agent", "mail", TrustLevel.Trusted, "grant", Ct);
        var file = Directory.GetFiles(_dir).Single();
        File.WriteAllText(file, """{ "agentType": "agent", "domain": "mail" }""");

        var ex = await Should.ThrowAsync<InvalidDataException>(() => new FileTrustStore(_dir).GetTrustLevelAsync("agent", "mail", Ct));

        ex.Message.ShouldContain(Path.GetFileName(file));
    }

    [Theory]
    [InlineData("../escape", "a/b")]
    [InlineData("agent:with*odd?chars", "do main")]
    public async Task NamesThatAreNotFileNames_StayInsideTheTrustFolder(string agentType, string domain)
    {
        await new FileTrustStore(_dir).SetTrustLevelAsync(agentType, domain, TrustLevel.Provisional, "grant", Ct);

        (await new FileTrustStore(_dir).GetTrustLevelAsync(agentType, domain, Ct)).ShouldBe(TrustLevel.Provisional);
        Directory.GetFiles(_dir).Length.ShouldBe(1);
        Directory.GetDirectories(_dir).ShouldBeEmpty();
    }
}
