using Ampelum.Samples.ExpenseApproval;
using Shouldly;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Xunit;

namespace Ampelum.Samples.ExpenseApproval.Tests;

/// <summary>Runs the sample against a real PostgreSQL database named by AMPELUM_TEST_POSTGRES. CI has no database and excludes this class by name.</summary>
public sealed class PostgresWorkflowPersistenceTests : IAsyncDisposable
{
    const string ConnectionVariable = "AMPELUM_TEST_POSTGRES";

    readonly string _connection = Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"{ConnectionVariable} is empty. Set it to the connection string of an isolated PostgreSQL database.");

    readonly List<string> _executionIds = [];
    readonly InMemoryExpenseLedger _ledger = new();

    string NewExecutionId()
    {
        var id = "expense-pg-" + Guid.NewGuid().ToString("N");
        _executionIds.Add(id);
        return id;
    }

    // Reads go straight to the library class so they cannot be satisfied by another store behind ExpenseStore.
    PostgresWorkflowPersistence Direct() => new(_connection);

    ExpenseApprovalPipeline CreatePipeline(Microsoft.Extensions.AI.IChatClient chat) =>
        new(chat, _ledger, ExpenseApprovalPipeline.LoadDefinition(), () => ExpenseStore.Postgres(_connection));

    [Fact]
    public async Task StartAndStopAsync_FirstRuntimeStopped_CheckpointRowIsInPostgres()
    {
        var id = NewExecutionId();
        var pipeline = CreatePipeline(FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel")));

        await using (await pipeline.StartAndStopAsync(id, "Taxi 42.50", TestContext.Current.CancellationToken))
        {
            var row = await Direct().LoadAsync(id, TestContext.Current.CancellationToken);

            row.ShouldNotBeNull();
            row.Status.ShouldBe(WorkflowExecutionStatus.Running);
            row.Nodes["extract"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
            row.Nodes["route"].Status.ShouldBe(DurableWorkflowNodeStatus.Running);
            _ledger.Paid.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task ResumeAsync_NewRuntimeAndPersistence_ResumesFromPostgresRowsWithoutRerunningExtract()
    {
        var id = NewExecutionId();
        var chat = FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"));
        var pipeline = CreatePipeline(chat);

        await using (await pipeline.StartAndStopAsync(id, "Taxi 42.50", TestContext.Current.CancellationToken))
        {
            var result = await pipeline.ResumeAsync(id, TestContext.Current.CancellationToken);

            result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
            result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.PayTool);
            _ledger.Paid.Count.ShouldBe(1);
            chat.RecordedCalls.Count.ShouldBe(1);
            result.Audit.Count(e => e.EventType == "node.started" && e.StepId == "extract").ShouldBe(1);
            result.Audit.ShouldContain(e => e.EventType == "node.recovered");
        }

        var reread = await Direct().LoadAsync(id, TestContext.Current.CancellationToken);
        reread.ShouldNotBeNull();
        reread.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        reread.Nodes["extract"].Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task RunWithRestartAsync_OverLimit_AuditRowsAreWrittenInOrder()
    {
        var id = NewExecutionId();
        var pipeline = CreatePipeline(FakeExpenseClient.Create(new Expense("DevSummit", 1800m, "training")));

        var result = await pipeline.RunWithRestartAsync("Ticket 1800", TestContext.Current.CancellationToken, id);

        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.ReviewTool);
        _ledger.Flagged.Count.ShouldBe(1);
        var audit = await Direct().QueryAuditAsync(id, TestContext.Current.CancellationToken);
        audit.Count.ShouldBeGreaterThan(0);
        audit.Select(e => e.Sequence).ShouldBe(audit.Select(e => e.Sequence).Order());
        audit.ShouldAllBe(e => e.ExecutionId == id);
        audit.First().EventType.ShouldBe("execution.started");
        audit.ShouldContain(e => e.EventType == "node.succeeded" && e.StepId == "extract");
        audit.Last().EventType.ShouldBe("execution.succeeded");
    }

    [Fact]
    public async Task Create_PostgresFlagWithSetting_WritesToPostgresAndNotToTheSqliteFile()
    {
        var id = NewExecutionId();
        var directory = Path.Combine(Path.GetTempPath(), "ampelum-expense-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sqlitePath = Path.Combine(directory, "workflows.db");
        try
        {
            var pipeline = new ExpenseApprovalPipeline(
                FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel")), _ledger, ExpenseApprovalPipeline.LoadDefinition(),
                () => ExpenseStore.Create(true, _ => _connection, sqlitePath));

            var result = await pipeline.RunWithRestartAsync("Taxi 42.50", TestContext.Current.CancellationToken, id);

            result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
            (await Direct().LoadAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
            File.Exists(sqlitePath).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FakePostgres_RowsReachPostgresNotSqliteAndAreGoneAfterTheRun()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ampelum-expense-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sqlitePath = Path.Combine(directory, "workflows.db");
        IReadOnlyList<string> ids = [];
        try
        {
            var outcome = await ExpenseRun.RunAsync(["--fake", "--postgres"], directory, _ => { }, _ => _connection, async created =>
            {
                ids = created;
                _executionIds.AddRange(created);
                created.Count.ShouldBe(2);
                foreach (var id in created)
                {
                    (await Direct().LoadAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
                    (await Direct().QueryAuditAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
                }
                File.Exists(sqlitePath).ShouldBeFalse();
            });

            outcome.ExitCode.ShouldBe(0);
            ids.Count.ShouldBe(2);
            foreach (var id in ids)
            {
                (await Direct().LoadAsync(id, TestContext.Current.CancellationToken)).ShouldBeNull();
                (await Direct().QueryAuditAsync(id, TestContext.Current.CancellationToken)).ShouldBeEmpty();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteExecutionAsync_RemovesCheckpointAndAuditRowsOfThatExecutionOnly()
    {
        var mine = NewExecutionId();
        var other = NewExecutionId();
        var pipeline = CreatePipeline(FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"), new Expense("Acme Taxi", 42.50m, "travel")));
        await pipeline.RunWithRestartAsync("one", TestContext.Current.CancellationToken, mine);
        await pipeline.RunWithRestartAsync("two", TestContext.Current.CancellationToken, other);
        var direct = Direct();
        (await direct.LoadAsync(mine, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await direct.QueryAuditAsync(mine, TestContext.Current.CancellationToken)).ShouldNotBeEmpty();

        await ExpenseStore.Postgres(_connection).DeleteExecutionAsync(mine, TestContext.Current.CancellationToken);

        (await direct.LoadAsync(mine, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await direct.QueryAuditAsync(mine, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await direct.LoadAsync(other, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await direct.QueryAuditAsync(other, TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        var store = ExpenseStore.Postgres(_connection);
        foreach (var id in _executionIds)
            await store.DeleteExecutionAsync(id);
    }
}
