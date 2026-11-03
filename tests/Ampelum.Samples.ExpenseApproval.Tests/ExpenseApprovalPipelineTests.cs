using Ampelum.Samples.ExpenseApproval;
using Microsoft.Data.Sqlite;
using Shouldly;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.ExpenseApproval.Tests;

public sealed class ExpenseApprovalPipelineTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "ampelum-expense-tests", Guid.NewGuid().ToString("N"));
    readonly InMemoryExpenseLedger _ledger = new();

    string DatabasePath
    {
        get
        {
            Directory.CreateDirectory(_directory);
            return Path.Combine(_directory, "workflows.db");
        }
    }

    ExpenseApprovalPipeline CreatePipeline(FakeChatClient chat, WorkflowDefinition? definition = null) =>
        new(chat, _ledger, definition ?? ExpenseApprovalPipeline.LoadDefinition(), DatabasePath);

    [Fact]
    public async Task RunWithRestartAsync_AmountUnderLimit_PaysOnceAndNeverFlagsForReview()
    {
        var chat = FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"));

        var result = await CreatePipeline(chat).RunWithRestartAsync("Taxi 42.50", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.PayTool);
        result.Expense.ShouldBe(new Expense("Acme Taxi", 42.50m, "travel"));
        _ledger.Paid.ShouldBe([new Expense("Acme Taxi", 42.50m, "travel")]);
        _ledger.Flagged.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunWithRestartAsync_AmountOverLimit_FlagsForReviewAndNeverPays()
    {
        var chat = FakeExpenseClient.Create(new Expense("DevSummit", 1800m, "training"));

        var result = await CreatePipeline(chat).RunWithRestartAsync("Ticket 1800", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.ReviewTool);
        _ledger.Flagged.ShouldBe([new Expense("DevSummit", 1800m, "training")]);
        _ledger.Paid.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeAsync_AfterStopOnNewPersistence_FinishesWithoutRerunningFirstStep()
    {
        var chat = FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"));
        var pipeline = CreatePipeline(chat);
        const string executionId = "expense-resume";

        await using (await pipeline.StartAndStopAsync(executionId, "Taxi 42.50", TestContext.Current.CancellationToken))
        {
            var stopped = await new SqliteWorkflowPersistence(DatabasePath).LoadAsync(executionId, TestContext.Current.CancellationToken);
            stopped.ShouldNotBeNull();
            stopped.Status.ShouldBe(WorkflowExecutionStatus.Running);
            stopped.Nodes["extract"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
            stopped.Nodes["route"].Status.ShouldBe(DurableWorkflowNodeStatus.Running);
            _ledger.Paid.ShouldBeEmpty();

            var result = await pipeline.ResumeAsync(executionId, TestContext.Current.CancellationToken);

            result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
            result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.PayTool);
            _ledger.Paid.Count.ShouldBe(1);
            chat.RecordedCalls.Count.ShouldBe(1);
            result.Audit.Count(e => e.EventType == "node.started" && e.StepId == "extract").ShouldBe(1);
            result.Audit.ShouldContain(e => e.EventType == "node.recovered");
            result.Audit.Last().EventType.ShouldBe("execution.succeeded");
        }

        var reread = await new SqliteWorkflowPersistence(DatabasePath).QueryAuditAsync(executionId, TestContext.Current.CancellationToken);
        reread.Select(e => e.EventType).ShouldContain("execution.succeeded");
        (await new SqliteWorkflowPersistence(DatabasePath).LoadAsync(executionId, TestContext.Current.CancellationToken))!
            .Nodes["extract"].Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task RunWithRestartAsync_ToolOutsideStepAllowList_IsBlockedAndNotInvoked()
    {
        var compiled = ExpenseApprovalPipeline.LoadDefinition();
        var blocked = compiled with
        {
            Steps = [.. compiled.Steps.Select(step => step.StepId == "pay" ? step with { ToolAllowList = [ExpenseApprovalPipeline.ReviewTool] } : step)],
        };
        var chat = FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"));

        var result = await CreatePipeline(chat, blocked).RunWithRestartAsync("Taxi 42.50", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Failed);
        result.FailureReason.ShouldNotBeNull().ShouldContain("allow-list");
        result.ToolExecuted.ShouldBeNull();
        _ledger.Paid.ShouldBeEmpty();
        _ledger.Flagged.ShouldBeEmpty();
        result.Audit.ShouldContain(e => e.EventType == "node.failed" && e.StepId == "pay");
    }

    [Fact]
    public async Task RunWithRestartAsync_LimitComesFromWorkflowYaml()
    {
        var compiled = ExpenseApprovalPipeline.LoadDefinition();
        var strict = compiled with
        {
            Steps = [.. compiled.Steps.Select(step => step.StepId == "route"
                ? step with { Inputs = new Dictionary<string, string> { ["limit"] = "10" } }
                : step)],
        };
        var chat = FakeExpenseClient.Create(new Expense("Acme Taxi", 42.50m, "travel"));

        var result = await CreatePipeline(chat, strict).RunWithRestartAsync("Taxi 42.50", TestContext.Current.CancellationToken);

        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.ReviewTool);
        _ledger.Paid.ShouldBeEmpty();
        _ledger.Flagged.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("500.00")]
    [InlineData("499.99")]
    public async Task RunWithRestartAsync_AmountAtOrUnderLimit_Pays(string amount)
    {
        var chat = FakeExpenseClient.Create("Acme", amount, "travel");

        var result = await CreatePipeline(chat).RunWithRestartAsync("note", TestContext.Current.CancellationToken);

        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.PayTool);
        _ledger.Paid.Count.ShouldBe(1);
        _ledger.Flagged.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("500.01", false)]
    [InlineData("500.0000000000001", false)]
    [InlineData("0", true)]
    [InlineData("-25", true)]
    [InlineData("abc", true)]
    public async Task RunWithRestartAsync_AmountOverLimitOrInvalid_FlagsForReviewAndNeverPays(string amount, bool hasReason)
    {
        var chat = FakeExpenseClient.Create("Acme", amount, "travel");

        var result = await CreatePipeline(chat).RunWithRestartAsync("note", TestContext.Current.CancellationToken);

        result.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.ToolExecuted.ShouldBe(ExpenseApprovalPipeline.ReviewTool);
        _ledger.Paid.ShouldBeEmpty();
        var flagged = _ledger.Flagged.ShouldHaveSingleItem();
        (flagged.ReviewReason is not null).ShouldBe(hasReason);
    }

    [Fact]
    public async Task RunWithRestartAsync_UnparseableAmount_ReviewEntryCarriesReason()
    {
        var chat = FakeExpenseClient.Create("Acme", "abc", "travel");

        await CreatePipeline(chat).RunWithRestartAsync("note", TestContext.Current.CancellationToken);

        _ledger.Flagged.ShouldHaveSingleItem().ReviewReason.ShouldNotBeNull().ShouldContain("abc");
    }

    [Fact]
    public async Task PayTool_SameIdempotencyKeyTwice_PaysOnce()
    {
        var pipeline = CreatePipeline(FakeExpenseClient.Create());
        var dispatcher = pipeline.CreateDispatcher(out var caller);
        var args = ExpenseApprovalPipeline.ToolArguments(new Expense("Acme", 42.50m, "travel"), "exec-1:pay");

        var first = await dispatcher.InvokeAsync(ExpenseApprovalPipeline.PayTool, args, caller, TestContext.Current.CancellationToken);
        var second = await dispatcher.InvokeAsync(ExpenseApprovalPipeline.PayTool, args, caller, TestContext.Current.CancellationToken);

        first.Success.ShouldBeTrue();
        second.Success.ShouldBeTrue();
        _ledger.Paid.Count.ShouldBe(1);

        var other = ExpenseApprovalPipeline.ToolArguments(new Expense("Acme", 42.50m, "travel"), "exec-2:pay");
        await dispatcher.InvokeAsync(ExpenseApprovalPipeline.PayTool, other, caller, TestContext.Current.CancellationToken);
        _ledger.Paid.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RunWithRestartAsync_PayReceivesTheNodeIdempotencyKey()
    {
        var keys = new KeyCapturingLedger();
        var pipeline = new ExpenseApprovalPipeline(
            FakeExpenseClient.Create(new Expense("Acme", 42.50m, "travel")), keys, ExpenseApprovalPipeline.LoadDefinition(), DatabasePath);

        var result = await pipeline.RunWithRestartAsync("note", TestContext.Current.CancellationToken);

        keys.PayKeys.ShouldBe([$"{result.ExecutionId}:pay"]);
    }

    [Fact]
    public void Create_PostgresFlagWithEmptySetting_FailsNamingTheSettingAndDoesNotFallBack()
    {
        var error = Should.Throw<InvalidOperationException>(() => ExpenseStore.Create(true, _ => "", DatabasePath));

        error.Message.ShouldContain("WORKFLOW_POSTGRES");
        File.Exists(DatabasePath).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_PostgresFlagWithEmptySetting_FailsNamingTheSettingAndWritesNothing()
    {
        var lines = new List<string>();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            ExpenseRun.RunAsync(["--fake", "--postgres"], _directory, lines.Add, _ => ""));

        error.Message.ShouldContain("WORKFLOW_POSTGRES");
        lines.ShouldBeEmpty();
        File.Exists(Path.Combine(_directory, "workflows.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_FakeWithoutPostgresFlag_UsesTheSqliteFileAndReadsNoSetting()
    {
        Directory.CreateDirectory(_directory);

        var outcome = await ExpenseRun.RunAsync(["--fake"], _directory, _ => { },
            key => throw new InvalidOperationException($"unexpected read of {key}"));

        outcome.ExitCode.ShouldBe(0);
        outcome.ExecutionIds.Count.ShouldBe(2);
        File.Exists(Path.Combine(_directory, "workflows.db")).ShouldBeTrue();
    }

    sealed class KeyCapturingLedger : IExpenseLedger
    {
        public List<string> PayKeys { get; } = [];

        public Task PayAsync(Expense expense, string idempotencyKey, CancellationToken ct)
        {
            PayKeys.Add(idempotencyKey);
            return Task.CompletedTask;
        }

        public Task FlagForReviewAsync(Expense expense, string idempotencyKey, CancellationToken ct) => Task.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

}
