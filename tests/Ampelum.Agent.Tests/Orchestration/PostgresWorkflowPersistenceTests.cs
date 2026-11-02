using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

[Collection("PostgresWorkflowPersistence")]
public sealed class PostgresWorkflowPersistenceTests
{
    [Fact]
    public async Task ResumeAsync_RecreatedPersistence_ExecutesOnlyRemainingNodeWithStableIdempotencyKey()
    {
        var request = CreateRequest("postgres-restart-" + Guid.NewGuid().ToString("N"), stepCount: 2);
        var firstStore = CreateStore();
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(
                step.StepId,
                step.StepId == "step-1" ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Pending,
                step.StepId == "step-1" ? 1 : 0,
                $"{request.ExecutionId}:{step.StepId}"));
        await firstStore.TryCreateAsync(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var restarted = CreateStore();
        DurableWorkflowNodeContext? executed = null;
        var result = await new DurableWorkflowRuntime(restarted, restarted).ResumeAsync(
            request,
            (node, _) =>
            {
                executed = node;
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        executed.ShouldNotBeNull();
        executed.Step.StepId.ShouldBe("step-2");
        executed.IdempotencyKey.ShouldBe($"{request.ExecutionId}:step-2");
        (await restarted.QueryAuditAsync(request.ExecutionId, TestContext.Current.CancellationToken)).Select(x => x.EventType).ShouldBe(["node.started", "node.succeeded", "execution.succeeded"]);
    }

    [Fact]
    public async Task RunAsync_TwoRecreatedPersistenceInstances_AtomicallyClaimOneNode()
    {
        var request = CreateRequest("postgres-concurrent-" + Guid.NewGuid().ToString("N"), stepCount: 1);
        var first = CreateStore();
        var second = CreateStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        var firstRun = new DurableWorkflowRuntime(first, first).RunAsync(
            request,
            async (_, ct) =>
            {
                Interlocked.Increment(ref executions);
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return DurableWorkflowNodeResult.Success();
            },
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var duplicateRun = new DurableWorkflowRuntime(second, second).RunAsync(
            request,
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);
        release.TrySetResult();

        await Task.WhenAll(firstRun, duplicateRun);
        executions.ShouldBe(1);
        (await second.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Nodes["step-1"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
    }

    [Fact]
    public async Task Runtime_RetryCancellationAndFailure_AuditOrderSurvivesPersistenceReload()
    {
        var prefix = "postgres-audit-" + Guid.NewGuid().ToString("N");
        var first = CreateStore();
        var retryAttempts = 0;
        await new DurableWorkflowRuntime(first, first, maxAttempts: 2).RunAsync(
            CreateRequest(prefix + "-retry", stepCount: 1),
            (_, _) => Task.FromResult(++retryAttempts == 1
                ? DurableWorkflowNodeResult.Failure("temporary", retryable: true)
                : DurableWorkflowNodeResult.Success("recovered")),
            TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await new DurableWorkflowRuntime(first, first).RunAsync(
            CreateRequest(prefix + "-cancel", stepCount: 1),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            cancellation.Token);

        await new DurableWorkflowRuntime(first, first).RunAsync(
            CreateRequest(prefix + "-failure", stepCount: 1),
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Failure("permanent")),
            TestContext.Current.CancellationToken);

        var restarted = CreateStore();
        var retryAudit = await restarted.QueryAuditAsync(prefix + "-retry", TestContext.Current.CancellationToken);
        var cancellationAudit = await restarted.QueryAuditAsync(prefix + "-cancel", TestContext.Current.CancellationToken);
        var failureAudit = await restarted.QueryAuditAsync(prefix + "-failure", TestContext.Current.CancellationToken);

        retryAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "node.retrying", "node.started", "node.succeeded", "execution.succeeded"]);
        cancellationAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "execution.cancelled"]);
        failureAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "node.failed", "execution.failed"]);
        retryAudit.Concat(cancellationAudit).Concat(failureAudit).Select(x => x.Sequence).Distinct().Count().ShouldBe(retryAudit.Count + cancellationAudit.Count + failureAudit.Count);
    }

    private static PostgresWorkflowPersistence CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("AMPELUM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set AMPELUM_TEST_POSTGRES to an isolated PostgreSQL database before running Postgres workflow persistence tests.");
        return new PostgresWorkflowPersistence(connectionString);
    }

    private static WorkflowExecutionRequest CreateRequest(string executionId, int stepCount) => new(
        executionId,
        new WorkflowDefinition("test", "v1", "Test", Enumerable.Range(1, stepCount)
            .Select(index => new WorkflowStepDefinition($"step-{index}", "test", index == 1 ? [] : [$"step-{index - 1}"]))
            .ToArray()),
        "tester",
        DateTimeOffset.UtcNow);
}

[CollectionDefinition("PostgresWorkflowPersistence", DisableParallelization = true)]
public sealed class PostgresWorkflowPersistenceCollection;
