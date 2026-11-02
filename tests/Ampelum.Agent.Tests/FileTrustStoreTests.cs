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
