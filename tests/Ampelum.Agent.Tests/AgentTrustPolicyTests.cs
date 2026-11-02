using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class AgentTrustPolicyTests
{
    private readonly AgentTrustPolicy _policy = new();

    // ── Catastrophic tier: always blocked (hard invariant) ───────────

    [Fact]
    public void Catastrophic_AtTrustNone_Blocked()
    {
        var tool = new ToolManifest
        {
            Name = "rm-rf",
            Description = "Delete everything",
            DangerTier = DangerTier.Catastrophic,
            Reversibility = Reversibility.Irreversible
        };

        var caller = new AgentIdentity("bot", "id1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.None, tool, caller);

        decision.Decision.ShouldBe(Decision.Block);
    }

    [Fact]
    public void Catastrophic_AtTrustTrusted_StillBlocked()
    {
        var tool = new ToolManifest
        {
            Name = "sudo-rm",
            Description = "System root access",
            DangerTier = DangerTier.Catastrophic,
            Reversibility = Reversibility.Irreversible
        };

        var caller = new AgentIdentity("bot", "id2", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Trusted, tool, caller);

        decision.Decision.ShouldBe(Decision.Block);
    }

    // ── Trust.None: all actions blocked ──────────────────────────────

    [Fact]
    public void TrustNone_SafeTool_Blocked()
    {
        var tool = new ToolManifest
        {
            Name = "read-file",
            Description = "Read a file",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id3", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.None, tool, caller);

        decision.Decision.ShouldBe(Decision.Block);
    }

    // ── Trust.Unknown: safe-tier only, RequireConfirmation for Mutating ──

    [Fact]
    public void TrustUnknown_SafeTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "list-dir",
            Description = "List directory",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id4", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Unknown, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void TrustUnknown_DestructiveTool_RequiresConfirmation()
    {
        var tool = new ToolManifest
        {
            Name = "write-file",
            Description = "Write a file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id5", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Unknown, tool, caller);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    [Fact]
    public void TrustUnknown_ExternalTool_RequiresConfirmation()
    {
        var tool = new ToolManifest
        {
            Name = "send-email",
            Description = "Send an email",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Irreversible
        };

        var caller = new AgentIdentity("bot", "id6", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Unknown, tool, caller);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    // ── Trust.Provisional: Mutating allowed, Destructive requires confirmation ──

    [Fact]
    public void TrustProvisional_SafeTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "read-file",
            Description = "Read a file",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id7", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Provisional, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void TrustProvisional_DestructiveTool_RequiresConfirmation()
    {
        var tool = new ToolManifest
        {
            Name = "write-file",
            Description = "Write a file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id8", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Provisional, tool, caller);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    // ── Trust.Established: Mutating auto-approved, Destructive requires confirmation ──

    [Fact]
    public void TrustEstablished_SafeTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "glob",
            Description = "Glob files",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id9", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Established, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void TrustEstablished_DestructiveTool_RequiresConfirmation()
    {
        var tool = new ToolManifest
        {
            Name = "git-commit",
            Description = "Commit to git",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.DurableWithUndo
        };

        var caller = new AgentIdentity("bot", "id10", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Established, tool, caller);

        decision.Decision.ShouldBe(Decision.RequireConfirmation);
    }

    // ── Trust.Trusted: most tools auto-approved ─────────────────────

    [Fact]
    public void TrustTrusted_SafeTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "read-file",
            Description = "Read a file",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id11", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Trusted, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void TrustTrusted_DestructiveTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "write-file",
            Description = "Write a file",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Reversible
        };

        var caller = new AgentIdentity("bot", "id12", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Trusted, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    [Fact]
    public void TrustTrusted_ExternalTool_Allowed()
    {
        var tool = new ToolManifest
        {
            Name = "deploy-app",
            Description = "Deploy to production",
            DangerTier = DangerTier.Destructive,
            Reversibility = Reversibility.Irreversible
        };

        var caller = new AgentIdentity("bot", "id13", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Trusted, tool, caller);

        decision.Decision.ShouldBe(Decision.Allow);
    }

    // ── Null arguments ───────────────────────────────────────────────

    [Fact]
    public void EvaluateByTrust_NullTool_Throws()
    {
        var caller = new AgentIdentity("bot", "id14", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        Should.Throw<ArgumentNullException>(
            () => _policy.EvaluateByTrust(TrustLevel.Trusted, null!, caller));
    }

    [Fact]
    public void EvaluateByTrust_NullCaller_Throws()
    {
        var tool = new ToolManifest
        {
            Name = "read-file",
            Description = "Read a file",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible
        };

        Should.Throw<ArgumentNullException>(
            () => _policy.EvaluateByTrust(TrustLevel.Trusted, tool, null!));
    }

    // ── Reason messages ──────────────────────────────────────────────

    [Fact]
    public void BlockedDecision_ContainsReason()
    {
        var tool = new ToolManifest
        {
            Name = "dangerous-tool",
            Description = "Very dangerous",
            DangerTier = DangerTier.Catastrophic,
            Reversibility = Reversibility.Irreversible
        };

        var caller = new AgentIdentity("bot", "id15", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var decision = _policy.EvaluateByTrust(TrustLevel.Trusted, tool, caller);

        decision.Reason.ShouldNotBeNull();
        decision.Reason.ShouldContain("Catastrophic");
    }
}
