using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class AgentTrustTests
{
    // ── TrustLevel ordering ─────────────────────────────────────────

    [Fact]
    public void TrustLevel_Values_OrderedByProgressiveTrust()
    {
        ((int)TrustLevel.None).ShouldBe(0);
        ((int)TrustLevel.Unknown).ShouldBe(1);
        ((int)TrustLevel.Provisional).ShouldBe(2);
        ((int)TrustLevel.Established).ShouldBe(3);
        ((int)TrustLevel.Trusted).ShouldBe(4);
    }

    // ── TrustPolicy defaults ────────────────────────────────────────

    [Fact]
    public void TrustPolicy_Defaults_AsPerSpec()
    {
        var policy = new TrustPolicy();

        policy.SuccessThresholdForEscalation.ShouldBe(5);
        policy.ViolationsForDemotion.ShouldBe(1);
        policy.EvaluationWindowRuns.ShouldBe(10);
        policy.ViolationsForRevocation.ShouldBe(3);
        policy.InactivityDecayDays.ShouldBe(90);
    }

    [Fact]
    public void TrustPolicy_CustomValues_Work()
    {
        var policy = new TrustPolicy
        {
            SuccessThresholdForEscalation = 10,
            ViolationsForDemotion = 2,
            EvaluationWindowRuns = 20,
            ViolationsForRevocation = 5,
            InactivityDecayDays = 180
        };

        policy.SuccessThresholdForEscalation.ShouldBe(10);
        policy.ViolationsForDemotion.ShouldBe(2);
        policy.EvaluationWindowRuns.ShouldBe(20);
        policy.ViolationsForRevocation.ShouldBe(5);
        policy.InactivityDecayDays.ShouldBe(180);
    }

    // ── AgentIdentity ───────────────────────────────────────────────

    [Fact]
    public void AgentIdentity_Constructor_SetsProperties()
    {
        var identity = new AgentIdentity(
            AgentType: "research-bot",
            InstanceId: "abc-123",
            Scope: ["read", "list"],
            IssuedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(50)
        );

        identity.AgentType.ShouldBe("research-bot");
        identity.InstanceId.ShouldBe("abc-123");
        identity.Scope.ShouldBe(["read", "list"]);
    }

    [Fact]
    public void AgentIdentity_TwoInstances_DifferentIds_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new AgentIdentity("bot", "id1", ["read"], now, now.AddHours(1));
        var b = new AgentIdentity("bot", "id2", ["read"], now, now.AddHours(1));

        a.ShouldNotBe(b);
    }

    // ── SessionGrant ────────────────────────────────────────────────

    [Fact]
    public void SessionGrant_IsExpired_FutureExpiry_ReturnsFalse()
    {
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "tool1",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test grant");

        grant.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public void SessionGrant_IsExpired_PastExpiry_ReturnsTrue()
    {
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "tool1",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            Reason: "expired grant");

        grant.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public void SessionGrant_CreatesWithAllFields()
    {
        var grantId = Guid.NewGuid().ToString();
        var grant = new SessionGrant(
            GrantId: grantId,
            CallerInstanceId: "caller-1",
            ToolName: "tool1",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "admin approval");

        grant.GrantId.ShouldBe(grantId);
        grant.CallerInstanceId.ShouldBe("caller-1");
        grant.ToolName.ShouldBe("tool1");
    }

    // ── TrustOutcome ────────────────────────────────────────────────

    [Fact]
    public void TrustOutcome_Record_StoresAllFields()
    {
        var now = DateTimeOffset.UtcNow;
        var outcome = new TrustOutcome(
            RunInstanceId: "run-123",
            Succeeded: true,
            HadSecurityViolation: false,
            HadEscalation: false,
            ToolCallCount: 42,
            OccurredAt: now
        );

        outcome.RunInstanceId.ShouldBe("run-123");
        outcome.Succeeded.ShouldBeTrue();
        outcome.HadSecurityViolation.ShouldBeFalse();
        outcome.HadEscalation.ShouldBeFalse();
        outcome.ToolCallCount.ShouldBe(42);
        outcome.OccurredAt.ShouldBe(now);
    }

    [Fact]
    public void TrustOutcome_WithViolation_RecordsIt()
    {
        var outcome = new TrustOutcome(
            RunInstanceId: "run-456",
            Succeeded: false,
            HadSecurityViolation: true,
            HadEscalation: true,
            ToolCallCount: 15,
            OccurredAt: DateTimeOffset.UtcNow
        );

        outcome.Succeeded.ShouldBeFalse();
        outcome.HadSecurityViolation.ShouldBeTrue();
        outcome.HadEscalation.ShouldBeTrue();
    }

    [Fact]
    public void TrustOutcome_Two_DifferentRunIds_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var outcome1 = new TrustOutcome("run-1", true, false, false, 10, now);
        var outcome2 = new TrustOutcome("run-2", true, false, false, 10, now);

        outcome1.ShouldNotBe(outcome2);
    }

    // ── IAgentTrustPolicy interface ──────────────────────────────────

    [Fact]
    public void IAgentTrustPolicy_IsInterface()
    {
        // Verify interface exists and can be implemented
        typeof(IAgentTrustPolicy).IsInterface.ShouldBeTrue();
    }

    [Fact]
    public void IAgentTrustPolicy_HasEvaluateByTrustMethod()
    {
        var method = typeof(IAgentTrustPolicy).GetMethod(
            nameof(IAgentTrustPolicy.EvaluateByTrust),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

        method.ShouldNotBeNull();
        method!.ReturnType.ShouldBe(typeof(PolicyDecision));
    }

    // ── IAgentTrustStore interface ───────────────────────────────────

    [Fact]
    public void IAgentTrustStore_HasGetTrustLevelAsync()
    {
        var method = typeof(IAgentTrustStore).GetMethod(
            nameof(IAgentTrustStore.GetTrustLevelAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

        method.ShouldNotBeNull();
    }

    [Fact]
    public void IAgentTrustStore_HasSetTrustLevelAsync()
    {
        var method = typeof(IAgentTrustStore).GetMethod(
            nameof(IAgentTrustStore.SetTrustLevelAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

        method.ShouldNotBeNull();
    }

    [Fact]
    public void IAgentTrustStore_HasRecordOutcomeAsync()
    {
        var method = typeof(IAgentTrustStore).GetMethod(
            nameof(IAgentTrustStore.RecordOutcomeAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

        method.ShouldNotBeNull();
    }

    [Fact]
    public void IAgentTrustStore_HasRevokeAsync()
    {
        var method = typeof(IAgentTrustStore).GetMethod(
            nameof(IAgentTrustStore.RevokeAsync),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

        method.ShouldNotBeNull();
    }
}