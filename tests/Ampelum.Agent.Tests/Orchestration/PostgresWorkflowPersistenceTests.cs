using Npgsql;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Orchestration;

[Collection("PostgresWorkflowPersistence")]
public sealed class PostgresWorkflowPersistenceTests : IAsyncDisposable
{
    // Every execution id a test writes starts with one of these; the rows are removed after each test because the
    // persistence has no delete API.
    private readonly List<string> _prefixes = [];

    [Fact]
    public async Task Constructor_ExistingSchemaWhileAWriterHoldsACheckpointRow_DoesNotWaitForTheWriter()
    {
        CreateStore();
        var executionId = Track("postgres-ctor-" + Guid.NewGuid().ToString("N"));
        await using var writer = new NpgsqlConnection(ConnectionString());
        await writer.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await writer.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO ampelum_workflow_checkpoints (execution_id, snapshot_json, version, updated_at_utc) VALUES (@id, '{}'::jsonb, 0, now());",
            writer, transaction))
        {
            insert.Parameters.AddWithValue("id", executionId);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var construction = Task.Run(CreateStore, TestContext.Current.CancellationToken);

        await construction.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Constructor_CheckpointTableWithoutVersionColumn_AddsTheColumnAndCreatesTheMissingTables()
    {
        var schema = "ampelum_test_" + Guid.NewGuid().ToString("N");
        var scoped = new NpgsqlConnectionStringBuilder(ConnectionString()) { SearchPath = schema }.ConnectionString;
        await using var admin = new NpgsqlConnection(ConnectionString());
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await Execute(admin, $"CREATE SCHEMA {schema}; CREATE TABLE {schema}.ampelum_workflow_checkpoints (execution_id TEXT PRIMARY KEY, snapshot_json JSONB NOT NULL, updated_at_utc TIMESTAMPTZ NOT NULL);");
        try
        {
            _ = new PostgresWorkflowPersistence(scoped);

            await using var read = new NpgsqlCommand($"""
                SELECT (SELECT count(*) FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = 'ampelum_workflow_checkpoints' AND column_name = 'version')
                     + (SELECT count(*) FROM information_schema.tables WHERE table_schema = '{schema}' AND table_name = 'ampelum_workflow_runtime_audit');
                """, admin);
            (await read.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(2L);
        }
        finally
        {
            await Execute(admin, $"DROP SCHEMA {schema} CASCADE;");
        }
    }

    [Fact]
    public async Task ResumeAsync_RecreatedPersistence_ExecutesOnlyRemainingNodeWithStableIdempotencyKey()
    {
        var request = CreateRequest(Track("postgres-restart-" + Guid.NewGuid().ToString("N")), stepCount: 2);
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
        var request = CreateRequest(Track("postgres-concurrent-" + Guid.NewGuid().ToString("N")), stepCount: 1);
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
        var prefix = Track("postgres-audit-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task WaitingNodeAndDecision_SurviveRecreatedPersistenceAndRuntime()
    {
        var request = ApprovalPauseResumeTests.ApprovalRequest(Track("postgres-approval-" + Guid.NewGuid().ToString("N")));
        var seen = new List<string>();
        var ct = TestContext.Current.CancellationToken;
        var first = CreateStore();
        (await new DurableWorkflowRuntime(first, first).RunAsync(request, ApprovalPauseResumeTests.Record(seen), ct)).Status
            .ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);

        var second = CreateStore();
        var stored = await second.LoadAsync(request.ExecutionId, ct);
        stored.ShouldNotBeNull().Status.ShouldBe(WorkflowExecutionStatus.AwaitingConfirmation);
        stored.Nodes["approve"].Status.ShouldBe(DurableWorkflowNodeStatus.Waiting);
        await new DurableWorkflowRuntime(second, second).DecideAsync(request.ExecutionId, "approve", true, "maria", "ok", ct);

        var third = CreateStore();
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

    private StepKindBackend StepKindBackend() => new(
        crash =>
        {
            var persistence = CreateStore();
            if (crash is null) return (persistence, persistence);
            var crashing = new CrashingTransactionStore(persistence, crash);
            return (crashing, crashing);
        },
        (id, ct) => CreateStore().QueryAuditAsync(id, ct),
        label => Track("postgres-" + label + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task WaitForSignal_CrashAfterDelivery_ResumesFromPostgres() =>
        await StepKindScenarios.SignalSurvivesACrashAfterDelivery(StepKindBackend());

    [Fact]
    public async Task Evaluate_CrashAfterObservations_ResumesFromPostgres() =>
        await StepKindScenarios.EvaluateSurvivesACrashAfterItsObservationsWereWritten(StepKindBackend());

    [Fact]
    public async Task EmitEvent_CrashAfterTheEvent_WritesItOnceOnPostgres() =>
        await StepKindScenarios.EmitEventIsWrittenOnceAcrossACrashAndResume(StepKindBackend());

    [Fact]
    public async Task Compensate_CrashBetweenTwoCompensations_ContinuesOnPostgres() =>
        await StepKindScenarios.CompensationContinuesAfterACrashBetweenTwoCompensations(StepKindBackend());

    [Fact]
    public async Task Load_CheckpointWrittenBeforeTheStepKindFields_LoadsWithTheFieldsDefaulted()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = CreateRequest(Track("postgres-legacy-" + Guid.NewGuid().ToString("N")), stepCount: 2);
        var nodes = request.Workflow.Steps.ToDictionary(
            step => step.StepId,
            step => new DurableWorkflowNodeSnapshot(step.StepId, step.StepId == "step-1" ? DurableWorkflowNodeStatus.Succeeded : DurableWorkflowNodeStatus.Pending, step.StepId == "step-1" ? 1 : 0, $"{request.ExecutionId}:{step.StepId}"));
        var legacyJson = LegacyCheckpoint.Json(new DurableWorkflowSnapshot(request, WorkflowExecutionStatus.Running, nodes, DateTimeOffset.UtcNow));
        var store = CreateStore();
        await using (var connection = new NpgsqlConnection(ConnectionString()))
        {
            await connection.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO ampelum_workflow_checkpoints (execution_id, snapshot_json, version, updated_at_utc) VALUES (@id, @json::jsonb, 0, now());", connection);
            insert.Parameters.AddWithValue("id", request.ExecutionId);
            insert.Parameters.AddWithValue("json", legacyJson);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var loaded = await store.LoadAsync(request.ExecutionId, ct);

        loaded.ShouldNotBeNull();
        loaded.Nodes.Values.ShouldAllBe(node => node.Signal == null && node.Observations == null && node.CompletionOrder == 0);
        var finished = await new DurableWorkflowRuntime(store, store).ResumeAsync(request, (_, _) => Task.FromResult(DurableWorkflowNodeResult.Success()), ct);
        finished.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        if (_prefixes.Count == 0) return;
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM ampelum_workflow_runtime_audit WHERE execution_id LIKE ANY(@patterns);
            DELETE FROM ampelum_workflow_checkpoints WHERE execution_id LIKE ANY(@patterns);
            """, connection);
        command.Parameters.AddWithValue("patterns", _prefixes.Select(p => p + "%").ToArray());
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private string Track(string prefix)
    {
        _prefixes.Add(prefix);
        return prefix;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("AMPELUM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set AMPELUM_TEST_POSTGRES to an isolated PostgreSQL database before running Postgres workflow persistence tests.");
        return connectionString;
    }

    private static PostgresWorkflowPersistence CreateStore() => new(ConnectionString());

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
