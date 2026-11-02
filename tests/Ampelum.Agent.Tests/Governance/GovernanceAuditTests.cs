using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class GovernanceAuditTests
{
    // ── GovernanceAuditEntry ────────────────────────────────────────────

    [Fact]
    public void GovernanceAuditEntry_Create_AllPropertiesSet()
    {
        var auditId = Guid.NewGuid().ToString();
        var actorId = Guid.NewGuid().ToString();
        var args = new Dictionary<string, object?> { { "path", "/tmp/file.txt" } };
        var timestamp = DateTimeOffset.UtcNow;
        var scopeId = "scope-123";

        var entry = new GovernanceAuditEntry(
            AuditId: auditId,
            ActorId: actorId,
            ActorType: "agent",
            ToolName: "read_file",
            Decision: PolicyDecision.Allowed,
            DecisionReason: "Safe tier, auto-approved",
            Args: args,
            Timestamp: timestamp,
            BlastRadiusScopeId: scopeId
        );

        entry.AuditId.ShouldBe(auditId);
        entry.ActorId.ShouldBe(actorId);
        entry.ActorType.ShouldBe("agent");
        entry.ToolName.ShouldBe("read_file");
        entry.Decision.Decision.ShouldBe(Decision.Allow);
        entry.DecisionReason.ShouldBe("Safe tier, auto-approved");
        entry.Args.ShouldBe(args);
        entry.Timestamp.ShouldBe(timestamp);
        entry.BlastRadiusScopeId.ShouldBe(scopeId);
    }

    [Fact]
    public void GovernanceAuditEntry_WithoutBlastRadiusScope_IsValid()
    {
        var entry = new GovernanceAuditEntry(
            AuditId: Guid.NewGuid().ToString(),
            ActorId: Guid.NewGuid().ToString(),
            ActorType: "agent",
            ToolName: "list_directory",
            Decision: PolicyDecision.ConfirmationRequired("Destructive operation"),
            DecisionReason: "Destructive + Irreversible",
            Args: new Dictionary<string, object?> { { "path", "/tmp" } },
            Timestamp: DateTimeOffset.UtcNow,
            BlastRadiusScopeId: null
        );

        entry.BlastRadiusScopeId.ShouldBeNull();
        entry.Decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void GovernanceAuditEntry_WithBlockDecision_PreservesReason()
    {
        var reason = "Catastrophic-tier tools are permanently blocked.";
        var entry = new GovernanceAuditEntry(
            AuditId: Guid.NewGuid().ToString(),
            ActorId: Guid.NewGuid().ToString(),
            ActorType: "agent",
            ToolName: "rm_rf",
            Decision: PolicyDecision.Blocked(reason),
            DecisionReason: reason,
            Args: new Dictionary<string, object?>(),
            Timestamp: DateTimeOffset.UtcNow
        );

        entry.Decision.Decision.ShouldBe(Decision.Block);
        entry.Decision.Reason.ShouldBe(reason);
    }

    // ── IToolManifestVerifier ────────────────────────────────────────────

    [Fact]
    public void VerificationResult_Create_PropertiesSet()
    {
        var reason = "Signature verified against trusted registry";
        var result = new VerificationResult(
            Trusted: true,
            AssignedTier: TrustLevel.Trusted,
            Reason: reason
        );

        result.Trusted.ShouldBeTrue();
        result.AssignedTier.ShouldBe(TrustLevel.Trusted);
        result.Reason.ShouldBe(reason);
    }

    [Fact]
    public void VerificationResult_Untrusted_WithReason()
    {
        var result = new VerificationResult(
            Trusted: false,
            AssignedTier: TrustLevel.None,
            Reason: "Signature missing or invalid"
        );

        result.Trusted.ShouldBeFalse();
        result.AssignedTier.ShouldBe(TrustLevel.None);
    }

    [Fact]
    public void StubVerifier_TrustsBuiltInSources()
    {
        var verifier = new StubToolManifestVerifier();
        var manifest = new ToolManifest
        {
            Name = "read_file",
            Description = "Read a file",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var result = verifier.Verify(manifest, "builtin://yaml");

        result.Trusted.ShouldBeTrue();
        result.AssignedTier.ShouldBe(TrustLevel.Trusted);
    }

    [Fact]
    public void StubVerifier_BlocksRemoteUnsignedTools()
    {
        var verifier = new StubToolManifestVerifier();
        var manifest = new ToolManifest
        {
            Name = "dangerous_tool",
            Description = "Unknown remote tool",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Irreversible
        };

        var result = verifier.Verify(manifest, "remote://untrusted.registry.com");

        result.Trusted.ShouldBeFalse();
        result.AssignedTier.ShouldBe(TrustLevel.None);
    }

    // ── SessionGrant ─────────────────────────────────────────────────────

    [Fact]
    public void SessionGrant_Create_PropertiesSet()
    {
        var grantId = Guid.NewGuid().ToString();
        var callerId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var grant = new SessionGrant(
            GrantId: grantId,
            CallerInstanceId: callerId,
            ToolName: "kubectl_delete",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: expiresAt,
            Reason: "Approved for namespace cleanup"
        );

        grant.GrantId.ShouldBe(grantId);
        grant.CallerInstanceId.ShouldBe(callerId);
        grant.ToolName.ShouldBe("kubectl_delete");
        grant.GrantedDecision.Decision.ShouldBe(Decision.Allow);
        grant.ExpiresAt.ShouldBe(expiresAt);
        grant.Reason.ShouldBe("Approved for namespace cleanup");
    }

    [Fact]
    public void SessionGrant_NotExpired_IsExpiredFalse()
    {
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: Guid.NewGuid().ToString(),
            ToolName: "test_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test"
        );

        grant.IsExpired.ShouldBeFalse();
    }

    [Fact]
    public void SessionGrant_Expired_IsExpiredTrue()
    {
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: Guid.NewGuid().ToString(),
            ToolName: "test_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            Reason: "test"
        );

        grant.IsExpired.ShouldBeTrue();
    }

    [Fact]
    public void SessionGrant_CanHaveConfirmationDecision()
    {
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: Guid.NewGuid().ToString(),
            ToolName: "destructive_tool",
            GrantedDecision: PolicyDecision.ConfirmationRequired("Promoted from Block"),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "User approved promotion"
        );

        grant.GrantedDecision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    // ── SessionGrantStore ────────────────────────────────────────────────

    [Fact]
    public void SessionGrantStore_Add_StoresGrant()
    {
        var store = new SessionGrantStore();
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "test_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test"
        );

        store.Add(grant);

        var found = store.FindActive("test_tool", "caller-1");
        found.ShouldNotBeNull();
        found.GrantId.ShouldBe(grant.GrantId);
    }

    [Fact]
    public void SessionGrantStore_FindActive_ReturnsNullForExpired()
    {
        var store = new SessionGrantStore();
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "test_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            Reason: "expired"
        );

        store.Add(grant);

        var found = store.FindActive("test_tool", "caller-1");
        found.ShouldBeNull();
    }

    [Fact]
    public void SessionGrantStore_FindActive_ReturnsNullForDifferentTool()
    {
        var store = new SessionGrantStore();
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "tool_a",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test"
        );

        store.Add(grant);

        var found = store.FindActive("tool_b", "caller-1");
        found.ShouldBeNull();
    }

    [Fact]
    public void SessionGrantStore_FindActive_ReturnsNullForDifferentCaller()
    {
        var store = new SessionGrantStore();
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "test_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test"
        );

        store.Add(grant);

        var found = store.FindActive("test_tool", "caller-2");
        found.ShouldBeNull();
    }

    [Fact]
    public void SessionGrantStore_RemoveExpired_CleansUpExpiredGrants()
    {
        var store = new SessionGrantStore();
        var activeGrant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "active",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "active"
        );
        var expiredGrant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: "caller-1",
            ToolName: "expired",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            Reason: "expired"
        );

        store.Add(activeGrant);
        store.Add(expiredGrant);

        store.RemoveExpired();

        store.FindActive("active", "caller-1").ShouldNotBeNull();
        store.FindActive("expired", "caller-1").ShouldBeNull();
    }

    // ── PolicyEngine with Grants ─────────────────────────────────────────

    [Fact]
    public void PolicyEngine_WithGrantStore_PromotesTool()
    {
        var caller = new AgentIdentity(
            AgentType: "test-bot",
            InstanceId: "instance-1",
            Scope: [],
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var store = new SessionGrantStore();
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: caller.InstanceId,
            ToolName: "destructive_tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "Approved for this session"
        );
        store.Add(grant);

        var engine = new PolicyEngine([], store);
        var tool = new ToolManifest
        {
            Name = "destructive_tool",
            Description = "A destructive tool",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Irreversible
        };
        var ctx = new PolicyContext { Caller = caller };

        var result = engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void PolicyEngine_WithCustomPolicy_BlocksBeforeTierDefault()
    {
        var caller = new AgentIdentity(
            AgentType: "test-bot",
            InstanceId: "instance-1",
            Scope: [],
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var customPolicy = new BlockToolPolicy("forbidden_tool");
        var engine = new PolicyEngine([customPolicy], null);

        var tool = new ToolManifest
        {
            Name = "forbidden_tool",
            Description = "A safe tool that is custom-blocked",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };
        var ctx = new PolicyContext { Caller = caller };

        var result = engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Block);
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("Custom policy blocks");
    }

    [Fact]
    public void PolicyEngine_NoArgsCtor_StillWorks()
    {
        var engine = new PolicyEngine();
        var caller = new AgentIdentity(
            AgentType: "test-bot",
            InstanceId: "instance-1",
            Scope: [],
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var tool = new ToolManifest
        {
            Name = "safe_tool",
            Description = "A safe tool",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };
        var ctx = new PolicyContext { Caller = caller };

        var result = engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }
}

/// <summary>Stub verifier for testing — trusts built-in sources, rejects remote unsigned tools.</summary>
internal sealed class StubToolManifestVerifier : IToolManifestVerifier
{
    public VerificationResult Verify(ToolManifest manifest, string sourcePrefix)
    {
        if (sourcePrefix.StartsWith("builtin://") || sourcePrefix.StartsWith("local://"))
        {
            return new VerificationResult(true, TrustLevel.Trusted, "Built-in source");
        }

        return new VerificationResult(false, TrustLevel.None, "Remote source not verified");
    }
}

/// <summary>Custom policy that blocks a specific tool by name.</summary>
internal sealed class BlockToolPolicy : IToolPolicy
{
    private readonly string _toolName;

    public BlockToolPolicy(string toolName)
    {
        _toolName = toolName;
    }

    public PolicyDecision Evaluate(ToolManifest tool, AgentIdentity caller, IReadOnlyDictionary<string, object?> args)
    {
        if (tool.Name == _toolName)
        {
            return PolicyDecision.Blocked($"Custom policy blocks tool '{_toolName}'");
        }

        return PolicyDecision.Allowed;
    }
}
