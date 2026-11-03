using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class WorkflowKindTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Loop_RepeatsWhileModelSaysRevise_UntilTheDeclaredBound()
    {
        var result = await RunAsync(new FakeTriageModel(_ => "revise"));

        var refine = result.Stored.Nodes["refine"];
        refine.LoopIterations.ShouldBe(2);
        refine.Attempts.ShouldBe(2);
        result.Outcome.EditorNotes.Count.ShouldBe(2);
        result.Outcome.WorkflowAudit.Count(e => e.EventType == "node.loop.iterated" && e.StepId == "refine").ShouldBe(1);
    }

    [Fact]
    public async Task Loop_StopsAfterOnePassWhenModelSaysOk()
    {
        var result = await RunAsync(new FakeTriageModel(_ => "ok"));

        result.Stored.Nodes["refine"].LoopIterations.ShouldBe(1);
        result.Outcome.EditorNotes.Count.ShouldBe(1);
        result.Outcome.WorkflowAudit.ShouldNotContain(e => e.EventType == "node.loop.iterated");
    }

    [Fact]
    public async Task FanOut_IsOneDurableNode_WorkersAreOnlyInputsAndTheHandlerDoesTheFanning()
    {
        var result = await RunAsync(new FakeTriageModel());

        var fanOut = result.Plan.Definition.Steps.Single(s => s.Kind == WorkflowStepKind.FanOut);
        fanOut.Inputs.ShouldNotBeNull()["worker.0.lane"].ShouldBe("backlog");
        result.Stored.Nodes.Keys.ShouldBe(result.Plan.Definition.Steps.Select(s => s.StepId), ignoreOrder: true);
        result.Outcome.Assessed.Results.Select(r => r.Issue).ShouldBe([101, 102, 103]);
    }

    [Fact]
    public async Task ApprovalAndWait_AreOrdinarySteps_TheRuntimeNeverPausesForThem()
    {
        var seen = new List<WorkflowStepKind>();
        var definition = new WorkflowDefinition("pause-check", "1.0.0", "Pause check",
        [
            new WorkflowStepDefinition("approve", "approve", [], Kind: WorkflowStepKind.Approval, MaxAutonomy: AutonomyMode.RequireApproval),
            new WorkflowStepDefinition("hold", "hold", ["approve"], Kind: WorkflowStepKind.Wait),
        ]);
        var audit = new InMemoryWorkflowRuntimeAuditSink();
        var runtime = new DurableWorkflowRuntime(new InMemoryWorkflowStateStore(), audit);

        var snapshot = await runtime.RunAsync(
            new WorkflowExecutionRequest("pause-check", definition, "test", DateTimeOffset.UtcNow),
            (context, _) =>
            {
                seen.Add(context.Step.Kind);
                return Task.FromResult(DurableWorkflowNodeResult.Success());
            },
            TestContext.Current.CancellationToken);

        snapshot.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        seen.ShouldBe([WorkflowStepKind.Approval, WorkflowStepKind.Wait]);
        audit.Events.Select(e => e.Status).Distinct().ShouldNotContain(WorkflowExecutionStatus.AwaitingConfirmation);
    }

    [Fact]
    public void Supervisor_RefusesPhaseChangeWhileWaitingForPerson_AndCompletesOnlyFromRunning()
    {
        var supervisor = new RunSupervisor("run-1", "assess");
        supervisor.Enter("refine");
        supervisor.WaitForPerson(TriageTools.PostReport, "Approve posting the triage report");

        supervisor.State.ShouldBe(WorkflowState.Suspended);
        supervisor.Waiting.ShouldNotBeNull().Type.ShouldBe(EscalationType.RequiresConfirmation);
        Should.Throw<InvalidOperationException>(() => supervisor.Enter("report"));
        Should.Throw<InvalidOperationException>(() => supervisor.Finish(succeeded: true));

        supervisor.PersonDecided();
        supervisor.Enter("report");
        supervisor.Count(total: 6, completed: 6, failed: 0);
        supervisor.Finish(succeeded: true);

        supervisor.State.ShouldBe(WorkflowState.Completed);
        supervisor.Phase.ShouldBe("report");
        supervisor.Progress.ShouldBe((6, 6, 0));
        Should.Throw<InvalidOperationException>(() => supervisor.Enter("again"));
    }

    [Fact]
    public async Task Monitor_ReportsBudgetBreachesAsRiskEvents()
    {
        var policy = TriagePolicy.Load(_workspace.Root) with { MaxModelCalls = 4 };

        var result = await RunAsync(new FakeTriageModel(), policy);

        result.Outcome.Health.SampleSize.ShouldBe(3);
        result.Outcome.Health.SuccessRate.ShouldBe(2d / 3d, 0.001);
        result.Outcome.Health.Concerns.ShouldNotBeNull().ShouldContain(c => c.Contains("risk-constraint breach"));
    }

    async Task<(DurableWorkflowSnapshot Stored, TriageOutcome Outcome, PlannedWorkflow Plan)> RunAsync(FakeTriageModel model, TriagePolicy? policy = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await TriageScenario.RunAsync(_workspace.Directory, model, FakeTriageModel.Tiers, policy: policy, ct: ct);
        var stored = await new SqliteWorkflowPersistence(Path.Combine(_workspace.State, "workflows.db")).LoadAsync(scenario.ExecutionId, ct);
        return (stored!, scenario.Outcome, scenario.Plan);
    }
}
