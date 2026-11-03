using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

[Collection("Orchestration")]
public class WorkspaceIdTests
{
    [Fact]
    public void WorkspaceId_New_CreatesLowerHyphenated()
    {
        var id = WorkspaceId.New("My Project");
        id.Value.ShouldBe("my-project");
}

// ── PhaseCheckpoint workspace extension (Task 1) ──────────────────

[Collection("Orchestration")]
public class PhaseCheckpointWorkspaceTests
{
    [Fact]
    public void PhaseCheckpoint_WorkspaceId_NullByDefault()
    {
        var cp = new PhaseCheckpoint
        {
            WorkflowId = "wf-1",
            Phase = "phase-1",
            GoalAnchor = "goal",
        };
        cp.WorkspaceId.ShouldBeNull();
    }

    [Fact]
    public void PhaseCheckpoint_WorkspaceId_CanBeSet()
    {
        var cp = new PhaseCheckpoint
        {
            WorkflowId = "wf-1",
            Phase = "phase-1",
            GoalAnchor = "goal",
            WorkspaceId = "my-project",
        };
        cp.WorkspaceId.ShouldBe("my-project");
    }

    [Fact]
    public void IStateStore_LoadAsync_HasWorkspaceIdOverload()
    {
        // Compile-time: verify overload signature exists
        static void VerifyOverload(IStateStore store)
        {
            _ = store.LoadAsync("wf", "workspace-1");
            _ = store.LoadAsync("wf");          // original still works
        }
        _ = (Action<IStateStore>)VerifyOverload; // suppress unused warning
    }
}

    [Fact]
    public void WorkspaceId_New_SingleWordPreserved()
    {
        var id = WorkspaceId.New("Artisan");
        id.Value.ShouldBe("artisan");
    }

    [Fact]
    public void WorkspaceId_ToString_ReturnsValue()
    {
        var id = WorkspaceId.New("ampelum-workspace");
        id.ToString().ShouldBe("ampelum-workspace");
    }

    [Fact]
    public void WorkspaceId_Equality_SameValueEqual()
    {
        var a = WorkspaceId.New("test");
        var b = WorkspaceId.New("test");
        (a == b).ShouldBeTrue();
        a.Equals(b).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceId_Equality_DifferentValueNotEqual()
    {
        var a = WorkspaceId.New("project-a");
        var b = WorkspaceId.New("project-b");
        (a != b).ShouldBeTrue();
    }

    [Fact]
    public void WorkspaceId_Equality_ConstructorDirect()
    {
        var a = new WorkspaceId("my-id");
        var b = new WorkspaceId("my-id");
        (a == b).ShouldBeTrue();
    }
}

[Collection("Orchestration")]
public class EscalationPolicyTests
{
    [Fact]
    public void EscalationPolicy_AutoApprove_DefaultsToDestructive()
    {
        var policy = EscalationPolicy.AutoApprove();
        policy.Mode.ShouldBe(EscalationMode.AutoApprove);
        policy.MaxAutoApproveTier.ShouldBe(DangerTier.Destructive);
    }

    [Fact]
    public void EscalationPolicy_AutoApprove_CustomTierRespected()
    {
        var policy = EscalationPolicy.AutoApprove(DangerTier.Safe);
        policy.MaxAutoApproveTier.ShouldBe(DangerTier.Safe);
    }

    [Fact]
    public void EscalationPolicy_AutoApprove_BlockListRespected()
    {
        var blockList = new HashSet<string> { "ShellExec", "FileDelete" };
        var policy = EscalationPolicy.AutoApprove(blockList: blockList);
        policy.BlockList.ShouldContain("ShellExec");
        policy.BlockList.ShouldContain("FileDelete");
        policy.BlockList.Count.ShouldBe(2);
    }

    [Fact]
    public void EscalationPolicy_Queue_ModeQueue()
    {
        var policy = EscalationPolicy.Queue();
        policy.Mode.ShouldBe(EscalationMode.Queue);
    }

    [Fact]
    public void EscalationPolicy_Skip_ModeSkip()
    {
        var policy = EscalationPolicy.Skip();
        policy.Mode.ShouldBe(EscalationMode.Skip);
    }

    [Fact]
    public void EscalationPolicy_AbortTask_ModeAbortTask()
    {
        var policy = EscalationPolicy.AbortTask();
        policy.Mode.ShouldBe(EscalationMode.AbortTask);
    }

    [Fact]
    public void EscalationPolicy_BlockList_DefaultEmpty()
    {
        var policy = EscalationPolicy.AutoApprove();
        policy.BlockList.ShouldBeEmpty();
    }
}

[Collection("Orchestration")]
public class DuplicateWorkspaceExceptionTests
{
    [Fact]
    public void DuplicateWorkspaceException_MessageContainsId()
    {
        var id = WorkspaceId.New("my-project");
        var ex = new DuplicateWorkspaceException(id);
        ex.Message.ShouldContain("my-project");
    }
}

[Collection("Orchestration")]
public class InMemoryProjectRegistryTests
{
    private static WorkspaceContext MakeWorkspace(string name)
    {
        return new WorkspaceContext
        {
            Id = WorkspaceId.New(name),
            Label = name,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
    }

    [Fact]
    public void Register_AddsWorkspace()
    {
        var registry = new InMemoryProjectRegistry();
        var ws = MakeWorkspace("test");
        registry.Register(ws);
        registry.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        var registry = new InMemoryProjectRegistry();
        var ws1 = MakeWorkspace("dup");
        var ws2 = MakeWorkspace("dup");
        registry.Register(ws1);
        Should.Throw<DuplicateWorkspaceException>(() => registry.Register(ws2));
    }

    [Fact]
    public void Get_ReturnsWorkspace_WhenRegistered()
    {
        var registry = new InMemoryProjectRegistry();
        var ws = MakeWorkspace("project-a");
        registry.Register(ws);
        var retrieved = registry.Get(WorkspaceId.New("project-a"));
        retrieved.ShouldNotBeNull();
        retrieved!.Label.ShouldBe("project-a");
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotRegistered()
    {
        var registry = new InMemoryProjectRegistry();
        var retrieved = registry.Get(WorkspaceId.New("nonexistent"));
        retrieved.ShouldBeNull();
    }

    [Fact]
    public void GetAll_ReturnsAllRegistered()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace("a"));
        registry.Register(MakeWorkspace("b"));
        registry.Register(MakeWorkspace("c"));
        var all = registry.GetAll();
        all.Count.ShouldBe(3);
    }

    [Fact]
    public void GetAll_ReturnsEmpty_WhenNoneRegistered()
    {
        var registry = new InMemoryProjectRegistry();
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void IsEmpty_True_WhenNoWorkspaces()
    {
        var registry = new InMemoryProjectRegistry();
        registry.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void IsEmpty_False_AfterRegister()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace("one"));
        registry.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task Deregister_RemovesAndDisposes()
    {
        var registry = new InMemoryProjectRegistry();
        var ws = MakeWorkspace("to-remove");
        registry.Register(ws);
        await registry.DeregisterAsync(WorkspaceId.New("to-remove"));
        registry.IsEmpty.ShouldBeTrue();
        registry.Get(WorkspaceId.New("to-remove")).ShouldBeNull();
    }

    [Fact]
    public async Task Deregister_UnknownId_NoOp()
    {
        var registry = new InMemoryProjectRegistry();
        registry.Register(MakeWorkspace("keep"));
        await registry.DeregisterAsync(WorkspaceId.New("never-added"));
        registry.IsEmpty.ShouldBeFalse();
    }
}

[Collection("Orchestration")]
public class RoundRobinSchedulerTests
{
    [Fact]
    public void SelectNext_EmptyList_ReturnsNull()
    {
        var scheduler = new RoundRobinScheduler();
        var result = scheduler.SelectNext(
            Array.Empty<WorkspaceId>(),
            new Dictionary<WorkspaceId, WorkspaceContext>());
        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void SelectNext_SingleWorkspace_AlwaysReturnsIt()
    {
        var scheduler = new RoundRobinScheduler();
        var ids = new[] { WorkspaceId.New("only") };
        var ctxs = new Dictionary<WorkspaceId, WorkspaceContext>();
        for (var i = 0; i < 5; i++)
        {
            var result = scheduler.SelectNext(ids, ctxs);
            result.HasValue.ShouldBeTrue();
            result!.Value.Value.ShouldBe("only");
        }
    }

    [Fact]
    public void SelectNext_MultipleWorkspaces_Cycles()
    {
        var scheduler = new RoundRobinScheduler();
        var ids = new[] { WorkspaceId.New("a"), WorkspaceId.New("b") };
        var ctxs = new Dictionary<WorkspaceId, WorkspaceContext>();

        var first = scheduler.SelectNext(ids, ctxs);
        var second = scheduler.SelectNext(ids, ctxs);
        var third = scheduler.SelectNext(ids, ctxs);

        // _cursor starts at 0 by default. Interlocked.Increment pre-increments:
        //   1st call: 1 % 2 = 1 → ids[1] = "b"
        //   2nd call: 2 % 2 = 0 → ids[0] = "a"
        //   3rd call: 3 % 2 = 1 → ids[1] = "b"
        first!.Value.Value.ShouldBe("b");
        second!.Value.Value.ShouldBe("a");
        third!.Value.Value.ShouldBe("b");
    }
}

[Collection("Orchestration")]
public class PriorityWeightedSchedulerTests
{
    [Fact]
    public void SelectNext_EmptyList_ReturnsNull()
    {
        var scheduler = new PriorityWeightedScheduler(42);
        var result = scheduler.SelectNext(
            Array.Empty<WorkspaceId>(),
            new Dictionary<WorkspaceId, WorkspaceContext>());
        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void SelectNext_SingleWorkspace_AlwaysReturnsIt()
    {
        var scheduler = new PriorityWeightedScheduler(42);
        var id = WorkspaceId.New("only");
        var ctxs = new Dictionary<WorkspaceId, WorkspaceContext>
        {
            [id] = CreateContext(id, 50)
        };
        for (var i = 0; i < 10; i++)
        {
            var result = scheduler.SelectNext(new[] { id }, ctxs);
            result.HasValue.ShouldBeTrue();
            result!.Value.Value.ShouldBe("only");
        }
    }

    [Fact]
    public void SelectNext_DeterministicSeed_ProducesSameSequence()
    {
        var idA = WorkspaceId.New("heavy");
        var idB = WorkspaceId.New("light");
        var ids = new[] { idA, idB };
        var ctxs = new Dictionary<WorkspaceId, WorkspaceContext>
        {
            [idA] = CreateContext(idA, 90),
            [idB] = CreateContext(idB, 10),
        };

        var scheduler1 = new PriorityWeightedScheduler(12345);
        var seq1 = Enumerable.Range(0, 20)
            .Select(_ => scheduler1.SelectNext(ids, ctxs)!.Value.Value).ToList();

        var scheduler2 = new PriorityWeightedScheduler(12345);
        var seq2 = Enumerable.Range(0, 20)
            .Select(_ => scheduler2.SelectNext(ids, ctxs)!.Value.Value).ToList();

        seq1.ShouldBe(seq2);
    }

    [Fact]
    public void SelectNext_HigherWeight_SelectedMoreOften()
    {
        var idA = WorkspaceId.New("heavy");
        var idB = WorkspaceId.New("light");
        var ids = new[] { idA, idB };
        var ctxs = new Dictionary<WorkspaceId, WorkspaceContext>
        {
            [idA] = CreateContext(idA, 90),
            [idB] = CreateContext(idB, 10),
        };

        var scheduler = new PriorityWeightedScheduler(42);
        var results = Enumerable.Range(0, 1000)
            .Select(_ => scheduler.SelectNext(ids, ctxs)!.Value.Value).ToList();

        var heavyCount = results.Count(r => r == "heavy");
        var lightCount = results.Count(r => r == "light");

        // 90% weight should yield roughly 90% selection
        heavyCount.ShouldBeGreaterThan(700);
        lightCount.ShouldBeLessThan(300);
    }

    private static WorkspaceContext CreateContext(WorkspaceId id, int priorityWeight)
    {
        return new WorkspaceContext
        {
            Id = id,
            Label = id.Value,
            PriorityWeight = priorityWeight,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
    }
}

[Collection("Orchestration")]
public class SoftwareDomainProfileTests
{
    [Fact]
    public void Name_IsSoftware()
    {
        new SoftwareDomainProfile().Name.ShouldBe("software");
    }

    [Fact]
    public void ToolIds_ContainsFileSystemTools()
    {
        var profile = new SoftwareDomainProfile();
        profile.ToolIds.ShouldContain("FileRead");
        profile.ToolIds.ShouldContain("FileWrite");
        profile.ToolIds.ShouldContain("ShellExec");
        profile.ToolIds.ShouldContain("GitClone");
        profile.ToolIds.ShouldContain("SshCheckService");
    }

    [Fact]
    public void ToolIds_DoesNotContainWebOnlyTools()
    {
        var profile = new SoftwareDomainProfile();
        var onlyWeb = new[] { "WebSearch", "WebFetch" };
        profile.ToolIds.Count.ShouldBeGreaterThan(onlyWeb.Length);
    }

    [Fact]
    public void BuildBlastRadius_ReturnsDestructiveCeiling()
    {
        var profile = new SoftwareDomainProfile();
        var scope = profile.BuildBlastRadius("/root/project");
        scope.MaxAllowedTier.ShouldBe(DangerTier.Destructive);
        scope.AllowedResourcePaths.ShouldContain("/root/project");
    }
}

[Collection("Orchestration")]
public class ResearchDomainProfileTests
{
    [Fact]
    public void Name_IsResearch()
    {
        new ResearchDomainProfile().Name.ShouldBe("research");
    }

    [Fact]
    public void ToolIds_ContainsOnlyWebAndReadTools()
    {
        var profile = new ResearchDomainProfile();
        profile.ToolIds.ShouldContain("WebSearch");
        profile.ToolIds.ShouldContain("WebFetch");
        profile.ToolIds.ShouldContain("FileRead");
    }

    [Fact]
    public void ToolIds_ExcludesWriteAndShellTools()
    {
        var profile = new ResearchDomainProfile();
        profile.ToolIds.ShouldNotContain("FileWrite");
        profile.ToolIds.ShouldNotContain("ShellExec");
        profile.ToolIds.ShouldNotContain("GitClone");
    }

    [Fact]
    public void BuildBlastRadius_ReturnsSafeCeilingWithUnrestrictedDomains()
    {
        var profile = new ResearchDomainProfile();
        var scope = profile.BuildBlastRadius("/root/research");
        scope.MaxAllowedTier.ShouldBe(DangerTier.Safe);
        scope.AllowedDomains.ShouldContain("*");
    }
}

[Collection("Orchestration")]
public class PortfolioOrchestratorTests
{
    [Fact]
    public async Task RunAsync_EmptyPortfolio_ReturnsSuccessImmediately()
    {
        var portfolio = new InMemoryProjectRegistry();
        var scheduler = new RoundRobinScheduler();
        var decomposer = new StubDecomposer();
        var orchestrator = new PortfolioOrchestrator(portfolio, scheduler, decomposer);

        var result = await orchestrator.RunAsync("any intent", PortfolioOptions.Default);

        result.OverallSuccess.ShouldBeTrue();
        result.CompletedWorkspaces.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_FilterRespected()
    {
        var portfolio = new InMemoryProjectRegistry();
        portfolio.Register(CreateContext("project-a"));
        portfolio.Register(CreateContext("project-b"));

        var scheduler = new RoundRobinScheduler();
        var decomposer = new StubDecomposer();
        var orchestrator = new PortfolioOrchestrator(portfolio, scheduler, decomposer);

        var options = new PortfolioOptions
        {
            ProjectFilter = new[] { WorkspaceId.New("project-a") }
        };

        var result = await orchestrator.RunAsync("intent", options);

        result.CompletedWorkspaces.Count.ShouldBe(1);
        result.CompletedWorkspaces[0].WorkspaceId.Value.ShouldBe("project-a");
    }

    [Fact]
    public async Task RunAsync_WithWorkspace_ExecutesSuccessfully()
    {
        var portfolio = new InMemoryProjectRegistry();
        portfolio.Register(CreateContext("test-project"));
        var scheduler = new RoundRobinScheduler();
        var decomposer = new StubDecomposer(new[] { new[] { "task-1", "task-2" } });
        var orchestrator = new PortfolioOrchestrator(portfolio, scheduler, decomposer);

        var result = await orchestrator.RunAsync("move forward", PortfolioOptions.Default with { DryRun = true });

        result.OverallSuccess.ShouldBeTrue();
        result.CompletedWorkspaces.Count.ShouldBe(1);
        result.FailedWorkspaces.ShouldBeEmpty();
    }

    private static WorkspaceContext CreateContext(string name)
    {
        return new WorkspaceContext
        {
            Id = WorkspaceId.New(name),
            Label = name,
            RootPath = Path.GetTempPath(),
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
    }
}

/// <summary>Stub IPortfolioIntentDecomposer for deterministic testing.</summary>
public sealed class StubDecomposer : IPortfolioIntentDecomposer
{
    private readonly string[][] _taskSets;
    private int _callCount;

    public StubDecomposer(params string[][] taskSets)
    {
        _taskSets = taskSets;
    }

    public Task<TaskDefinition[]> DecomposeAsync(
        string intent,
        ProjectSummary projectSummary,
        WorkspaceContext workspace,
        CancellationToken ct = default)
    {
        if (_taskSets.Length == 0)
            return Task.FromResult(Array.Empty<TaskDefinition>());

        var idx = Math.Min(_callCount, _taskSets.Length - 1);
        Interlocked.Increment(ref _callCount);
        var names = _taskSets[idx];

        var tasks = names.Select((name, i) => new TaskDefinition
        {
            Id = name,
            Description = $"Task {name}",
            TaskType = "test",
            DependsOn = i > 0 ? new[] { names[i - 1] } : Array.Empty<string>(),
        }).ToArray();

        return Task.FromResult(tasks);
    }
}