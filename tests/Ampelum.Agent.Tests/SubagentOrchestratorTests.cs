using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class SubagentOrchestratorTests
{
    private static TaskNode MakeNode(string id, string description, string taskType, params string[] dependsOn) =>
        new()
        {
            Id = id,
            Description = description,
            TaskType = taskType,
            DependsOn = new HashSet<string>(dependsOn)
        };

    private static TaskExecutionResult MakeResult(bool success, string? error = null) =>
        new()
        {
            Success = success,
            Error = error
        };

    // ── Simple execution ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SingleNode_ReturnsResult()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[] { MakeNode("n1", "Do something", "read_file") };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
            Task.FromResult(MakeResult(true)));

        results.Count.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MultipleIndependent_RunsAll()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[]
        {
            MakeNode("n1", "Step 1", "read"),
            MakeNode("n2", "Step 2", "write"),
            MakeNode("n3", "Step 3", "list")
        };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
            Task.FromResult(MakeResult(true)));

        results.All(r => r.Success).ShouldBeTrue();
        results.Count.ShouldBe(3);
    }

    // ── DAG dependencies ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DependentNode_WaitsForParent()
    {
        var orchestrator = new SubagentOrchestrator();
        var executionOrder = new List<string>();
        var nodes = new[]
        {
            MakeNode("parent", "Parent", "build"),
            MakeNode("child", "Child", "test", dependsOn: ["parent"])
        };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
        {
            executionOrder.Add(node.Id);
            return Task.FromResult(MakeResult(true));
        });

        results.All(r => r.Success).ShouldBeTrue();
        executionOrder.IndexOf("parent").ShouldBeLessThan(executionOrder.IndexOf("child"));
    }

    [Fact]
    public async Task ExecuteAsync_ChainOfThree_ExecutesInOrder()
    {
        var orchestrator = new SubagentOrchestrator();
        var executionOrder = new List<string>();
        var nodes = new[]
        {
            MakeNode("a", "A", "init"),
            MakeNode("b", "B", "build", dependsOn: ["a"]),
            MakeNode("c", "C", "test", dependsOn: ["b"])
        };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
        {
            executionOrder.Add(node.Id);
            return Task.FromResult(MakeResult(true));
        });

        results.All(r => r.Success).ShouldBeTrue();
        executionOrder.ShouldBe(["a", "b", "c"]);
    }

    // ── Failure propagation ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailedParent_SkipsChild()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[]
        {
            MakeNode("parent", "Parent", "build"),
            MakeNode("child", "Child", "test", dependsOn: ["parent"])
        };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
            node.Id == "parent"
                ? Task.FromResult(MakeResult(false, "build failed"))
                : Task.FromResult(MakeResult(true)));

        results[0].Success.ShouldBeFalse();
        results[0].Error.ShouldBe("build failed");
        results[1].Success.ShouldBeFalse();
        results[1].Error.ShouldBe("blocked by failed dependency");
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionInExecutor_CapturesError()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[] { MakeNode("n1", "Fail", "crash") };

        var results = await orchestrator.ExecuteAsync(nodes, (node, ct) =>
            throw new InvalidOperationException("crash!"));

        results[0].Success.ShouldBeFalse();
        results[0].Error.ShouldBe("crash!");
    }

    // ── Cancellation ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Cancelled_ThrowsOperationCanceled()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[] { MakeNode("n1", "Cancel", "slow") };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            orchestrator.ExecuteAsync(nodes, (node, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(MakeResult(true));
            }, cts.Token));
    }

    // ── Deadlock detection ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MissingDependency_ThrowsDeadlock()
    {
        var orchestrator = new SubagentOrchestrator();
        var nodes = new[]
        {
            MakeNode("orphan", "Orphan", "task", dependsOn: ["nonexistent"])
        };

        await Should.ThrowAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteAsync(nodes, (node, ct) =>
                Task.FromResult(MakeResult(true))));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyNodes_ReturnsEmpty()
    {
        var orchestrator = new SubagentOrchestrator();

        var results = await orchestrator.ExecuteAsync([], (node, ct) =>
            Task.FromResult(MakeResult(true)));

        results.ShouldBeEmpty();
    }
}