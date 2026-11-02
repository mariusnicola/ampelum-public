using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SuccessEvaluatorTests
{
    // ── DefaultSuccessEvaluator.EvaluateAsync ─────────────────────

    [Fact]
    public async Task EvaluateAsync_SuccessfulTask_ReturnsScore1()
    {
        var evaluator = new DefaultSuccessEvaluator();
        var task = new TaskDefinition { Id = "t1", Description = "do thing", TaskType = "implement" };
        var result = new TaskExecutionResult { Success = true, Output = "done" };

        var judgment = await evaluator.EvaluateAsync(task, result);

        judgment.Score.ShouldBe(1.0);
        judgment.Passed.ShouldBeTrue();
        judgment.Summary.ShouldContain("completed successfully");
    }

    [Fact]
    public async Task EvaluateAsync_FailedTask_ReturnsScore0()
    {
        var evaluator = new DefaultSuccessEvaluator();
        var task = new TaskDefinition { Id = "t1", Description = "do thing", TaskType = "implement" };
        var result = new TaskExecutionResult { Success = false, Error = "build broke" };

        var judgment = await evaluator.EvaluateAsync(task, result);

        judgment.Score.ShouldBe(0.0);
        judgment.Passed.ShouldBeFalse();
        judgment.Summary.ShouldContain("failed");
        judgment.ActionItems.ShouldNotBeNull();
        judgment.ActionItems.Count.ShouldBeGreaterThan(0);
    }

    // ── DefaultSuccessEvaluator.EvaluatePortfolioAsync ────────────

    [Fact]
    public async Task EvaluatePortfolioAsync_AllPassed_ReturnsScore1()
    {
        var evaluator = new DefaultSuccessEvaluator();
        var ws = new WorkspaceContext
        {
            Id = WorkspaceId.New("test"),
            Label = "test",
            RootPath = ".",
            PriorityWeight = 50,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
        var results = new[]
        {
            new TaskExecutionResult { Success = true, Output = "a" },
            new TaskExecutionResult { Success = true, Output = "b" },
            new TaskExecutionResult { Success = true, Output = "c" },
        };

        var judgment = await evaluator.EvaluatePortfolioAsync(ws, results);

        judgment.Score.ShouldBe(1.0);
        judgment.Passed.ShouldBeTrue();
        judgment.Summary.ShouldBe("3/3 tasks completed successfully");
    }

    [Fact]
    public async Task EvaluatePortfolioAsync_SomeFailed_ReturnsPartialScore()
    {
        var evaluator = new DefaultSuccessEvaluator();
        var ws = new WorkspaceContext
        {
            Id = WorkspaceId.New("test"),
            Label = "test",
            RootPath = ".",
            PriorityWeight = 50,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
        var results = new[]
        {
            new TaskExecutionResult { Success = true, Output = "a" },
            new TaskExecutionResult { Success = false, Error = "fail" },
            new TaskExecutionResult { Success = true, Output = "c" },
        };

        var judgment = await evaluator.EvaluatePortfolioAsync(ws, results);

        judgment.Score.ShouldBe(2.0 / 3.0);
        judgment.Passed.ShouldBeFalse();
        judgment.Summary.ShouldBe("2/3 tasks completed successfully");
        judgment.ActionItems.ShouldNotBeNull();
        judgment.ActionItems.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluatePortfolioAsync_NoTasks_ReturnsScore1()
    {
        var evaluator = new DefaultSuccessEvaluator();
        var ws = new WorkspaceContext
        {
            Id = WorkspaceId.New("test"),
            Label = "test",
            RootPath = ".",
            PriorityWeight = 50,
            MaxBlastRadius = new BlastRadiusScope(),
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };

        var judgment = await evaluator.EvaluatePortfolioAsync(ws, Array.Empty<TaskExecutionResult>());

        judgment.Score.ShouldBe(1.0);
        judgment.Passed.ShouldBeTrue();
        judgment.Summary.ShouldBe("No tasks executed");
    }

    // ── TaskDefinition.SuccessCriteria ────────────────────────────

    [Fact]
    public void TaskDefinition_SuccessCriteria_DefaultNull()
    {
        var task = new TaskDefinition { Id = "t1", Description = "test", TaskType = "test" };
        task.SuccessCriteria.ShouldBeNull();
    }

    [Fact]
    public void TaskDefinition_SuccessCriteria_CanBeSet()
    {
        var task = new TaskDefinition
        {
            Id = "t1",
            Description = "test",
            TaskType = "test",
            SuccessCriteria = "At least 5 peer-reviewed sources",
        };
        task.SuccessCriteria.ShouldBe("At least 5 peer-reviewed sources");
    }

    // ── WorkspaceExecutionResult.Judgment ─────────────────────────

    [Fact]
    public void WorkspaceExecutionResult_Judgment_DefaultNull()
    {
        var result = new WorkspaceExecutionResult
        {
            WorkspaceId = WorkspaceId.New("test"),
            Success = true,
            TaskResults = Array.Empty<TaskExecutionResult>(),
        };
        result.Judgment.ShouldBeNull();
    }

    // ── PortfolioOrchestrator with ISuccessEvaluator ──────────────

    [Fact]
    public async Task PortfolioRun_WithEvaluator_PopulatesJudgment()
    {
        var registry = new InMemoryProjectRegistry();
        var ws = new WorkspaceContext
        {
            Id = WorkspaceId.New("test"),
            Label = "Test",
            RootPath = Path.GetTempPath(),
            PriorityWeight = 50,
            MaxBlastRadius = new BlastRadiusScope(),
            AllowedToolIds = new HashSet<string> { "FileRead", "FileWrite" },
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
        registry.Register(ws);

        var orchestrator = new PortfolioOrchestrator(
            registry,
            new RoundRobinScheduler(),
            new TestStubDecomposer(),
            successEvaluator: new DefaultSuccessEvaluator());

        var result = await orchestrator.RunAsync("test intent",
            new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = true });

        result.OverallSuccess.ShouldBeTrue();
        foreach (var wsResult in result.CompletedWorkspaces)
        {
            wsResult.Judgment.ShouldNotBeNull();
            wsResult.Judgment!.Score.ShouldBe(1.0);
            wsResult.Judgment.Passed.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task PortfolioRun_WithoutEvaluator_JudgmentNull()
    {
        var registry = new InMemoryProjectRegistry();
        var ws = new WorkspaceContext
        {
            Id = WorkspaceId.New("test"),
            Label = "Test",
            RootPath = Path.GetTempPath(),
            PriorityWeight = 50,
            MaxBlastRadius = new BlastRadiusScope(),
            AllowedToolIds = new HashSet<string> { "FileRead", "FileWrite" },
            TokenStore = new InMemoryTokenStore(),
            SecretStore = new InMemorySecretStore(),
            Skills = new SkillRegistry(),
        };
        registry.Register(ws);

        var orchestrator = new PortfolioOrchestrator(
            registry,
            new RoundRobinScheduler(),
            new TestStubDecomposer());

        var result = await orchestrator.RunAsync("test intent",
            new PortfolioOptions { MaxConcurrentProjects = 1, DryRun = true });

        foreach (var wsResult in result.CompletedWorkspaces)
        {
            wsResult.Success.ShouldBeTrue();
            wsResult.Judgment.ShouldBeNull();
        }
    }
}

/// <summary>Stub decomposer for test — returns one noop task.</summary>
internal sealed class TestStubDecomposer : IPortfolioIntentDecomposer
{
    public Task<TaskDefinition[]> DecomposeAsync(
        string intent, ProjectSummary summary, WorkspaceContext workspace,
        CancellationToken ct = default)
    {
        return Task.FromResult(new[]
        {
            new TaskDefinition
            {
                Id = "test-task",
                Description = $"Test task in {workspace.Label}",
                TaskType = "noop",
                DependsOn = Array.Empty<string>(),
            },
        });
    }
}
