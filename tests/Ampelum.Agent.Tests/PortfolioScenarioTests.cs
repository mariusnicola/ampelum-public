using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

[Collection("Scenarios")]
public class PortfolioScenarioTests : IAsyncDisposable
{
    private readonly string _dirA = Path.Combine(Path.GetTempPath(), $"ws-a-{Guid.NewGuid():N}");
    private readonly string _dirB = Path.Combine(Path.GetTempPath(), $"ws-b-{Guid.NewGuid():N}");

    public PortfolioScenarioTests()
    {
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);

        // Seed project A
        File.WriteAllText(Path.Combine(_dirA, "roadmap.md"),
            "## Phase 3 — Feature Development\n- [x] Auth complete\n- [ ] Pagination pending");
        File.WriteAllText(Path.Combine(_dirA, "backlog.md"),
            "- [ ] Add user pagination\n- [ ] Fix null reference\n- [ ] Update README");

        // Seed project B — no backlog file, only roadmap
        File.WriteAllText(Path.Combine(_dirB, "roadmap.md"),
            "## Phase 1 — Bootstrap\n- [ ] Initial scaffolding");
    }

    [Fact]
    public async Task PortfolioRun_TwoProjects_BothComplete()
    {
        if (Environment.GetEnvironmentVariable("AMPELUM_E2E_SCENARIOS") != "true")
            return; // Auto-skip when not in scenario mode

        var registry = new InMemoryProjectRegistry();

        registry.Register(new WorkspaceContext
        {
            Id = WorkspaceId.New("project-a"),
            Label = "Project A",
            RootPath = _dirA,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        });

        registry.Register(new WorkspaceContext
        {
            Id = WorkspaceId.New("project-b"),
            Label = "Project B",
            RootPath = _dirB,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        });

        var decomposer = new StubDecomposer(
            new[] { "review-backlog" },
            new[] { "review-roadmap" });

        var orchestrator = new PortfolioOrchestrator(
            registry,
            new RoundRobinScheduler(),
            decomposer);

        var result = await orchestrator.RunAsync(
            "advance on all projects",
            PortfolioOptions.Default with { DryRun = true },
            TestContext.Current.CancellationToken);

        result.OverallSuccess.ShouldBeTrue();
        result.CompletedWorkspaces.Count.ShouldBe(2);
        result.FailedWorkspaces.ShouldBeEmpty();

        var wsAResult = result.CompletedWorkspaces
            .FirstOrDefault(r => r.WorkspaceId == WorkspaceId.New("project-a"));
        wsAResult.ShouldNotBeNull();
    }

    [Fact]
    public async Task PortfolioRun_FailureInOneProject_OthersComplete()
    {
        if (Environment.GetEnvironmentVariable("AMPELUM_E2E_SCENARIOS") != "true")
            return;

        var registry = new InMemoryProjectRegistry();
        registry.Register(new WorkspaceContext
        {
            Id = WorkspaceId.New("ok-project"),
            Label = "OK Project",
            RootPath = _dirA,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        });
        registry.Register(new WorkspaceContext
        {
            Id = WorkspaceId.New("fail-project"),
            Label = "Fail Project",
            RootPath = _dirB,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        });

        var decomposer = new ThrowingDecomposer(throwOn: "fail-project");
        var orchestrator = new PortfolioOrchestrator(
            registry, new RoundRobinScheduler(), decomposer);

        var result = await orchestrator.RunAsync(
            "advance on all projects",
            PortfolioOptions.Default with { DryRun = true },
            TestContext.Current.CancellationToken);

        result.OverallSuccess.ShouldBeFalse();
        result.CompletedWorkspaces.Count.ShouldBe(1);
        result.FailedWorkspaces.Count.ShouldBe(1);
        result.FailedWorkspaces[0].Id.Value.ShouldBe("fail-project");
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dirA)) Directory.Delete(_dirA, recursive: true);
        if (Directory.Exists(_dirB)) Directory.Delete(_dirB, recursive: true);
        await ValueTask.CompletedTask;
    }

    // ── Test double for failure scenario ──────────────────────────
    private sealed class ThrowingDecomposer(string throwOn) : IPortfolioIntentDecomposer
    {
        public Task<TaskDefinition[]> DecomposeAsync(
            string intent, ProjectSummary summary, WorkspaceContext workspace,
            CancellationToken ct = default)
        {
            if (workspace.Id.Value == throwOn)
                throw new InvalidOperationException("Simulated decomposer failure");
            return Task.FromResult(new[]
            {
                new TaskDefinition { Id = "t1", Description = "ok task", TaskType = "test" }
            });
        }
    }
}