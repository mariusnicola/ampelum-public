using Microsoft.Data.Sqlite;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

public sealed class SqliteWorkflowPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ampelum-workflow-persistence", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResumeAsync_RecreatedPersistence_ExecutesOnlyRemainingNodeWithStableIdempotencyKey()
    {
        var path = DatabasePath();
        var request = CreateRequest("sqlite-restart", stepCount: 2);
        var firstStore = new SqliteWorkflowPersistence(path);
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(
                step.StepId,
                step.StepId == "step-1" ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Pending,
                step.StepId == "step-1" ? 1 : 0,
                $"{request.ExecutionId}:{step.StepId}"));
        await firstStore.TryCreateAsync(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        var restarted = new SqliteWorkflowPersistence(path);
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
        executed.IdempotencyKey.ShouldBe("sqlite-restart:step-2");
        (await restarted.QueryAuditAsync(request.ExecutionId, TestContext.Current.CancellationToken)).Select(x => x.EventType).ShouldBe(["node.started", "node.succeeded", "execution.succeeded"]);
    }

    [Fact]
    public async Task RunAsync_TwoRecreatedPersistenceInstances_AtomicallyClaimOneNode()
    {
        var path = DatabasePath();
        var request = CreateRequest("sqlite-concurrent", stepCount: 1);
        var first = new SqliteWorkflowPersistence(path);
        var second = new SqliteWorkflowPersistence(path);
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
        var path = DatabasePath();
        var first = new SqliteWorkflowPersistence(path);
        var retryAttempts = 0;
        await new DurableWorkflowRuntime(first, first, maxAttempts: 2).RunAsync(
            CreateRequest("sqlite-retry", stepCount: 1),
            (_, _) => Task.FromResult(++retryAttempts == 1
                ? DurableWorkflowNodeResult.Failure("temporary", retryable: true)
                : DurableWorkflowNodeResult.Success("recovered")),
            TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await new DurableWorkflowRuntime(first, first).RunAsync(
            CreateRequest("sqlite-cancel", stepCount: 1),
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            cancellation.Token);

        await new DurableWorkflowRuntime(first, first).RunAsync(
            CreateRequest("sqlite-failure", stepCount: 1),
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Failure("permanent")),
            TestContext.Current.CancellationToken);

        var restarted = new SqliteWorkflowPersistence(path);
        var retryAudit = await restarted.QueryAuditAsync("sqlite-retry", TestContext.Current.CancellationToken);
        var cancellationAudit = await restarted.QueryAuditAsync("sqlite-cancel", TestContext.Current.CancellationToken);
        var failureAudit = await restarted.QueryAuditAsync("sqlite-failure", TestContext.Current.CancellationToken);

        retryAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "node.retrying", "node.started", "node.succeeded", "execution.succeeded"]);
        cancellationAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "execution.cancelled"]);
        failureAudit.Select(x => x.EventType).ShouldBe(["execution.started", "node.started", "node.failed", "execution.failed"]);
        retryAudit.Concat(cancellationAudit).Concat(failureAudit).Select(x => x.Sequence).Distinct().Count().ShouldBe(retryAudit.Count + cancellationAudit.Count + failureAudit.Count);
    }

    [Fact]
    public async Task Constructor_PreExistingDatabaseWithoutVersionColumn_MigratesAndAcceptsWrites()
    {
        // Simulates a database file created by a pre-`version`-column release (rc3 and earlier): the
        // constructor's schema step must add the missing column rather than leaving `CREATE TABLE IF NOT
        // EXISTS` as a no-op, or every subsequent write throws "no such column: version".
        var path = DatabasePath();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE workflow_checkpoints (
                    execution_id TEXT PRIMARY KEY,
                    snapshot_json TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL);
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new SqliteWorkflowPersistence(path);
        var request = CreateRequest("sqlite-pre-existing-schema", stepCount: 1);
        var result = await new DurableWorkflowRuntime(store, store).RunAsync(
            request,
            (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()),
            TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task TryUpdateAsync_StructurallyDifferentButSameVersionExpected_SucceedsOnVersionAlone()
    {
        // Same CAS-version regression as the in-memory store, exercised against the JSON-column-backed
        // provider: the WHERE clause must key off the `version` column, not `snapshot_json` text equality.
        var store = new SqliteWorkflowPersistence(DatabasePath());
        var request = CreateRequest("sqlite-cas-version", stepCount: 1);
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(step.StepId, DurableWorkflowNodeStatus.Pending, 0, $"{request.ExecutionId}:{step.StepId}"));
        var original = new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow, Version: 5);
        await store.TryCreateAsync(original, TestContext.Current.CancellationToken);

        var staleShapedExpected = new DurableWorkflowSnapshot(
            request,
            WorkflowExecutionStatus.Running,
            new Dictionary<string, DurableWorkflowNodeSnapshot>(nodes),
            original.UpdatedAt.AddSeconds(-1),
            Version: 5);
        var next = original with { Status = WorkflowExecutionStatus.Succeeded, Version = 6 };

        var updated = await store.TryUpdateAsync(request.ExecutionId, staleShapedExpected, next, TestContext.Current.CancellationToken);

        updated.ShouldBeTrue();
        (await store.LoadAsync(request.ExecutionId, TestContext.Current.CancellationToken))!.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task WaitingNodeAndDecision_SurviveRecreatedPersistenceAndRuntime()
    {
        var path = DatabasePath();
        var request = ApprovalPauseResumeTests.ApprovalRequest("sqlite-approval");
        var seen = new List<string>();
        var ct = TestContext.Current.CancellationToken;
        var first = new SqliteWorkflowPersistence(path);
        (await new DurableWorkflowRuntime(first, first).RunAsync(request, ApprovalPauseResumeTests.Record(seen), ct)).Status
            .ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);

        var second = new SqliteWorkflowPersistence(path);
        var stored = await second.LoadAsync(request.ExecutionId, ct);
        stored.ShouldNotBeNull().Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        stored.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        await new DurableWorkflowRuntime(second, second).DecideAsync(request.ExecutionId, "approve", true, "maria", "ok", ct);

        var third = new SqliteWorkflowPersistence(path);
        (await third.LoadAsync(request.ExecutionId, ct))!.Nodes["approve"].Decision.ShouldBe(new DurableWorkflowDecision(true, "maria", "ok"));
        var finished = await new DurableWorkflowRuntime(third, third).ResumeAsync(request, ApprovalPauseResumeTests.Record(seen), ct);

        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        seen.ShouldBe(["other", "after"]);
        (await third.QueryAuditAsync(request.ExecutionId, ct)).Select(x => x.EventType).ShouldBe(
        [
            "execution.started", "node.waiting", "node.started", "node.succeeded", "execution.awaiting_confirmation",
            "node.approved", "node.started", "node.succeeded", "execution.succeeded",
        ]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string DatabasePath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "workflows.db");
    }

    private static WorkflowExecutionRequest CreateRequest(string executionId, int stepCount) => new(
        executionId,
        new WorkflowDefinition("test", "v1", "Test", Enumerable.Range(1, stepCount)
            .Select(index => new WorkflowStepDefinition($"step-{index}", "test", index == 1 ? [] : [$"step-{index - 1}"]))
            .ToArray()),
        "tester",
        DateTimeOffset.UtcNow);
}
