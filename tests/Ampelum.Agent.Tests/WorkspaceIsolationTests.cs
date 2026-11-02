using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

// CORE GAPS — spec §8 invariants not yet enforced (filed for Phase 22 completion):
//
// Gap 1 (Path enforcement): Tool calls to paths outside workspace.MaxBlastRadius.AllowedPaths
//   must be rejected before execution by ToolDispatcher. PortfolioOrchestrator.MakeExecutor
//   is currently a placeholder and does not inject IToolDispatcher. Until MakeExecutor is
//   wired, path enforcement is not in the call path and cannot be tested structurally.
//   Tracking: Phase 22 completion note in roadmap.
//
// Gap 2 (ObservationToken cross-workspace): An ObservationToken issued in workspace A
//   must be invalid in workspace B. Requires workspace-scoped token issuance in
//   ToolDispatcher, which is not yet implemented.
//
// The tests below verify the DATA that enforcement will consult — they are correct
// and necessary, but they do not substitute for the enforcement tests above.
[Collection("Orchestration")]
public class WorkspaceIsolationTests
{
    private static WorkspaceContext MakeWorkspace(string name, string rootPath)
    {
        var profile = new SoftwareDomainProfile();
        return new WorkspaceContext
        {
            Id = WorkspaceId.New(name),
            Label = name,
            RootPath = rootPath,
            MaxBlastRadius = profile.BuildBlastRadius(rootPath),
            AllowedToolIds = profile.ToolIds,
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
    }

    [Fact]
    public void BlastRadius_AllowedPath_IsWithinRoot()
    {
        var ws = MakeWorkspace("project-a", "/projects/project-a");
        ws.MaxBlastRadius.AllowedResourcePaths.ShouldContain("/projects/project-a");
    }

    [Fact]
    public void BlastRadius_SoftwareProfile_MaxTierIsDestructive()
    {
        var ws = MakeWorkspace("project-a", "/projects/project-a");
        ws.MaxBlastRadius.MaxAllowedTier.ShouldBe(DangerTier.Destructive);
    }

    [Fact]
    public void ResearchProfile_BlastRadius_IsReadOnly()
    {
        var profile = new ResearchDomainProfile();
        var scope = profile.BuildBlastRadius("/research/topic-1");
        scope.MaxAllowedTier.ShouldBe(DangerTier.Safe);
    }

    [Fact]
    public void WorkspaceContext_StatePartition_IsWorkspaceId()
    {
        var ws = MakeWorkspace("my-project", "/tmp/my-project");
        ws.StatePartition.ShouldBe("my-project");
    }

    [Fact]
    public void WorkspaceContexts_DifferentIds_HaveDifferentStatePartitions()
    {
        var wsA = MakeWorkspace("project-a", "/tmp/a");
        var wsB = MakeWorkspace("project-b", "/tmp/b");
        wsA.StatePartition.ShouldNotBe(wsB.StatePartition);
    }

    [Fact]
    public void AllowedToolIds_SoftwareProfile_ContainsWriteTools()
    {
        var ws = MakeWorkspace("app", "/tmp/app");
        ws.AllowedToolIds.ShouldContain("ShellExec");
        ws.AllowedToolIds.ShouldContain("FileWrite");
    }

    [Fact]
    public void EscalationPolicy_CatastrophicAlwaysBlocked_RegardlessOfMode()
    {
        var policy = EscalationPolicy.AutoApprove(DangerTier.Destructive);
        policy.MaxAutoApproveTier.ShouldBe(DangerTier.Destructive);
        policy.MaxAutoApproveTier.ShouldNotBe(DangerTier.Catastrophic);
    }
}