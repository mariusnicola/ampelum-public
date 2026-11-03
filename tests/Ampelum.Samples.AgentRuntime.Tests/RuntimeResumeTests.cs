using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.Contracts;
using SignalGrid.Ampelum.Agent.Orchestration;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class RuntimeResumeTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task Scenario_StopsAtApprovalAndFinishesOnRestartedProcess_EventLogShowsBothSides()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await TriageScenario.RunAsync(_workspace.Directory, new FakeTriageModel(), FakeTriageModel.Tiers, ct: ct);

        result.PausedSnapshot.State.ShouldBe(WorkflowState.Suspended);
        result.PausedSnapshot.PendingInterrupt.ShouldNotBeNull().Prompt.ShouldBe("Approve posting the triage report");
        result.PausedSnapshot.PhaseContext[0].Content.ShouldContain(TriageScenario.Goal);
        result.Outcome.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        result.Outcome.RunEvents.Select(e => e.Kind).ShouldBe(
        [
            DurableRunEventKind.Started,
            DurableRunEventKind.CheckpointSaved,
            DurableRunEventKind.InterruptRequested,
            DurableRunEventKind.CheckpointSaved,
            DurableRunEventKind.Resumed,
            DurableRunEventKind.CheckpointSaved,
        ]);
        result.Outcome.RunEvents.Last().Snapshot.CurrentPhase.ShouldBe("report");
        File.Exists(result.Outcome.ReportPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Resume_RepeatsNoFinishedStepAndMakesNoModelCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeTriageModel();
        const string executionId = "resume-check";

        await using var first = _workspace.NewProcess(model);
        var plan = await first.PlanAsync(TriageScenario.Goal, ct);
        var runId = await first.StartRunAsync(TriageScenario.Goal, ct);
        await using var stopped = await first.RunUntilApprovalAsync(runId, executionId, plan.Definition, ct);
        var callsAtStop = model.Calls.Count;
        var stoppedAt = await first.Store.LoadAsync(executionId, ct);
        stoppedAt.ShouldNotBeNull();
        stoppedAt.Nodes["assess"].Status.ShouldBe(DurableWorkflowNodeStatus.Succeeded);
        stoppedAt.Nodes["signoff"].Status.ShouldBe(DurableWorkflowNodeStatus.Running);
        stoppedAt.Nodes["report"].Status.ShouldBe(DurableWorkflowNodeStatus.Pending);

        await using var second = _workspace.NewProcess(model);
        await second.ApproveAsync(runId, "Maintainer approved", ct);
        var outcome = await second.FinishAsync(runId, executionId, ct);

        outcome.Status.ShouldBe(WorkflowExecutionStatus.Succeeded);
        model.Calls.Count.ShouldBe(callsAtStop);
        var finished = await second.Store.LoadAsync(executionId, ct);
        finished.ShouldNotBeNull();
        finished.Nodes["assess"].Attempts.ShouldBe(1);
        finished.Nodes["collect"].Attempts.ShouldBe(1);
        finished.Nodes["refine"].Attempts.ShouldBe(2);
        finished.Nodes["signoff"].Attempts.ShouldBe(2);
        Started(outcome, "assess").ShouldBe(1);
        Started(outcome, "refine").ShouldBe(2);
        outcome.WorkflowAudit.Count(e => e.EventType == "node.recovered").ShouldBe(1);
        outcome.WorkflowAudit.Last().EventType.ShouldBe("execution.succeeded");
    }

    [Fact]
    public async Task Resume_WithTokenOfAnotherInterrupt_IsRefusedAndRunStaysSuspended()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeTriageModel();

        await using var first = _workspace.NewProcess(model);
        var plan = await first.PlanAsync(TriageScenario.Goal, ct);
        var runId = await first.StartRunAsync(TriageScenario.Goal, ct);
        await using var stopped = await first.RunUntilApprovalAsync(runId, "token-check", plan.Definition, ct);

        await using var second = _workspace.NewProcess(model);
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() =>
            second.Runtime.ResumeAsync(runId, new DurableResumeInput("not-the-token", "guess"), ct));
        refusal.Message.ShouldContain("does not match");
        (await second.Runtime.GetSnapshotAsync(runId, ct))!.State.ShouldBe(WorkflowState.Suspended);

        await Should.ThrowAsync<InvalidOperationException>(() => second.FinishAsync(runId, "token-check", ct));
    }

    [Fact]
    public async Task StreamAsync_OnRestartedRuntime_ReplaysStoredEventsAndEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new FakeTriageModel();

        await using var first = _workspace.NewProcess(model);
        var plan = await first.PlanAsync(TriageScenario.Goal, ct);
        var runId = await first.StartRunAsync(TriageScenario.Goal, ct);
        await using var stopped = await first.RunUntilApprovalAsync(runId, "stream-check", plan.Definition, ct);

        await using var second = _workspace.NewProcess(model);
        // The guard only keeps a regression from hanging the run; a healthy replay ends on its own.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        guard.CancelAfter(TimeSpan.FromSeconds(60));
        var replayed = new List<DurableRunEventKind>();
        await foreach (var evt in second.Runtime.StreamAsync(runId, guard.Token))
            replayed.Add(evt.Kind);

        replayed.ShouldBe([DurableRunEventKind.Started, DurableRunEventKind.CheckpointSaved, DurableRunEventKind.InterruptRequested, DurableRunEventKind.CheckpointSaved]);
    }

    static int Started(TriageOutcome outcome, string stepId) =>
        outcome.WorkflowAudit.Count(e => e.EventType == "node.started" && e.StepId == stepId);
}
