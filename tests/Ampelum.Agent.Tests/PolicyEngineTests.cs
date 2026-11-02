using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class PolicyEngineTests
{
    private readonly PolicyEngine _engine = new();
    private readonly AgentIdentity _caller = new(
        AgentType: "test-bot",
        InstanceId: Guid.NewGuid().ToString(),
        Scope: [],
        IssuedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
    );

    private ToolManifest MakeTool(DangerTier danger = DangerTier.Safe,
        Reversibility reversible = Reversibility.Reversible,
        bool requiresConfirmation = false)
    {
        return new ToolManifest
        {
            Name = "test-tool",
            Description = "A test tool",
            DangerTier = danger,
            Reversibility = reversible,
            RequiresConfirmation = requiresConfirmation,
            ArgsSchema = null,
            AllowedValues = null,
            OnFailure = OnFailureMode.Abort
        };
    }

    [Fact]
    public void Evaluate_CatastrophicTier_AlwaysBlocked()
    {
        var tool = MakeTool(DangerTier.Catastrophic);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Block);
        result.Reason!.ShouldContain("Catastrophic");
    }

    [Fact]
    public void Evaluate_CatastrophicTier_BlockedEvenWithSessionGrant()
    {
        var tool = MakeTool(DangerTier.Catastrophic);
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: _caller.InstanceId,
            ToolName: "test-tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test grant"
        );
        var ctx = new PolicyContext { Caller = _caller, SessionGrant = grant };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Evaluate_RequiresConfirmation_ReturnsConfirmationRequired()
    {
        var tool = MakeTool(DangerTier.Safe, requiresConfirmation: true);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void Evaluate_ExternalIrreversible_ConfirmationRequired()
    {
        var tool = MakeTool(DangerTier.Destructive, Reversibility.Irreversible);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void Evaluate_ExternalReversible_Allowed()
    {
        var tool = MakeTool(DangerTier.Destructive, Reversibility.Reversible);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_DestructiveIrreversible_ConfirmationRequired()
    {
        var tool = MakeTool(DangerTier.Destructive, Reversibility.Irreversible);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void Evaluate_DestructiveReversible_Allowed()
    {
        var tool = MakeTool(DangerTier.Destructive, Reversibility.Reversible);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_WithValidSessionGrant_AllowsSafeTool()
    {
        // Grant doesn't override rules 1-4, but for Safe/Reversible tools it's a no-op Allow
        var tool = MakeTool(DangerTier.Safe);
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: _caller.InstanceId,
            ToolName: "test-tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            Reason: "test grant"
        );
        var ctx = new PolicyContext { Caller = _caller, SessionGrant = grant };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_SessionGrant_ExpiredStillAllowsSafeTool()
    {
        // Expired grant doesn't affect evaluation of safe tool (rule 6 default)
        var tool = MakeTool(DangerTier.Safe);
        var grant = new SessionGrant(
            GrantId: Guid.NewGuid().ToString(),
            CallerInstanceId: _caller.InstanceId,
            ToolName: "test-tool",
            GrantedDecision: PolicyDecision.Allowed,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(-1),
            Reason: "expired grant"
        );
        var ctx = new PolicyContext { Caller = _caller, SessionGrant = grant };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_SafeTool_AllowedByDefault()
    {
        var tool = MakeTool(DangerTier.Safe);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_SensitiveTool_AllowedByDefault()
    {
        var tool = MakeTool(DangerTier.Additive);
        var ctx = new PolicyContext { Caller = _caller };

        var result = _engine.Evaluate(tool, ctx);

        result.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void Evaluate_NullTool_Throws()
    {
        var ctx = new PolicyContext { Caller = _caller };

        Should.Throw<ArgumentNullException>(() => _engine.Evaluate(null!, ctx));
    }

    [Fact]
    public void Evaluate_NullContext_Throws()
    {
        Should.Throw<ArgumentNullException>(() => _engine.Evaluate(MakeTool(), null!));
    }
}