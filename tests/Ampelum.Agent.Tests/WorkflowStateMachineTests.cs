using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class WorkflowStateMachineTests
{
    [Fact]
    public void Constructor_SetsInitialState()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        wf.WorkflowId.ShouldBe("wf-1");
        wf.CurrentPhase.ShouldBe("plan");
        wf.CurrentState.ShouldBe(WorkflowState.Running);
        wf.IsAutonomous.ShouldBeFalse();
    }

    [Fact]
    public void Constructor_Autonomous_SetsFlag()
    {
        var wf = new WorkflowStateMachine("wf-2", "execute", isAutonomous: true);

        wf.IsAutonomous.ShouldBeTrue();
    }

    [Fact]
    public void AdvancePhase_FromRunning_ChangesPhase()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.AdvancePhase("execute");

        wf.CurrentPhase.ShouldBe("execute");
        wf.CurrentState.ShouldBe(WorkflowState.Running);
    }

    [Fact]
    public void AdvancePhase_FromSuspended_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "write_file",
            "Needs approval", DateTimeOffset.UtcNow));

        Should.Throw<InvalidOperationException>(() => wf.AdvancePhase("execute"));
    }

    [Fact]
    public void AdvancePhase_FromCompleted_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Complete();

        Should.Throw<InvalidOperationException>(() => wf.AdvancePhase("done"));
    }

    [Fact]
    public void Escalate_MovesToSuspended_SetsContext()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        var ctx = new EscalationContext(
            EscalationType.BlastRadiusBreach, "write_file",
            "Path not allowed", DateTimeOffset.UtcNow);

        wf.Escalate(ctx);

        wf.CurrentState.ShouldBe(WorkflowState.Suspended);
        wf.CurrentEscalation.ShouldNotBeNull();
        wf.CurrentEscalation!.Type.ShouldBe(EscalationType.BlastRadiusBreach);
        wf.CurrentEscalation.ToolName.ShouldBe("write_file");
        wf.SuspendedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Escalate_FromSuspended_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow));

        Should.Throw<InvalidOperationException>(() => wf.Escalate(
            new EscalationContext(EscalationType.Custom, "t", "r", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Escalate_FromCompleted_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Complete();

        Should.Throw<InvalidOperationException>(() => wf.Escalate(
            new EscalationContext(EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void ResolveEscalation_ReturnsToRunning_ClearsContext()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "write_file", "Need ok", DateTimeOffset.UtcNow));

        wf.ResolveEscalation();

        wf.CurrentState.ShouldBe(WorkflowState.Running);
        wf.CurrentEscalation.ShouldBeNull();
        wf.SuspendedAt.ShouldBeNull();
    }

    [Fact]
    public void ResolveEscalation_FromRunning_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        Should.Throw<InvalidOperationException>(() => wf.ResolveEscalation());
    }

    [Fact]
    public void Complete_FromRunning_SetsCompleted()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Complete();

        wf.CurrentState.ShouldBe(WorkflowState.Completed);
    }

    [Fact]
    public void Complete_FromSuspended_Throws()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow));

        Should.Throw<InvalidOperationException>(() => wf.Complete());
    }

    [Fact]
    public void Fail_FromAnyState_Works()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Fail();
        wf.CurrentState.ShouldBe(WorkflowState.Failed);

        // Fail from suspended
        wf = new WorkflowStateMachine("wf-2", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow));
        wf.Fail();
        wf.CurrentState.ShouldBe(WorkflowState.Failed);
    }

    [Fact]
    public void Abort_FromAnyState_Works()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Abort();
        wf.CurrentState.ShouldBe(WorkflowState.Aborted);

        wf = new WorkflowStateMachine("wf-2", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow));
        wf.Abort();
        wf.CurrentState.ShouldBe(WorkflowState.Aborted);
    }

    [Fact]
    public void IsEscalationTimedOut_NotSuspended_ReturnsFalse()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        wf.IsEscalationTimedOut().ShouldBeFalse();
    }

    [Fact]
    public void IsEscalationTimedOut_WithinTimeout_ReturnsFalse()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "t", "r", DateTimeOffset.UtcNow));

        wf.IsEscalationTimedOut().ShouldBeFalse();
    }

    [Fact]
    public void IsEscalationTimedOut_PastTimeout_ReturnsTrue()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan")
        {
            EscalationTimeout = TimeSpan.FromMinutes(1)
        };
        // Set SuspendedAt to 2 minutes ago
        var past = DateTimeOffset.UtcNow.AddMinutes(-2);
        wf.GetType().GetProperty("SuspendedAt")!.SetValue(wf, past);
        wf.GetType().GetProperty("CurrentState")!.SetValue(wf, WorkflowState.Suspended);

        wf.IsEscalationTimedOut().ShouldBeTrue();
    }

    // ── WorkflowStateTransitions ────────────────────────────────────

    [Fact]
    public void Transitions_ContainsAllValidMoves()
    {
        WorkflowStateTransitions.Allowed.ShouldContain("Running→Suspended");
        WorkflowStateTransitions.Allowed.ShouldContain("Running→Completed");
        WorkflowStateTransitions.Allowed.ShouldContain("Running→Failed");
        WorkflowStateTransitions.Allowed.ShouldContain("Running→Aborted");
        WorkflowStateTransitions.Allowed.ShouldContain("Suspended→Running");
        WorkflowStateTransitions.Allowed.ShouldContain("Suspended→Aborted");
        WorkflowStateTransitions.Allowed.ShouldContain("Suspended→Failed");
    }

    [Fact]
    public void Transitions_Count_IsSeven()
    {
        WorkflowStateTransitions.Allowed.Count.ShouldBe(7);
    }

    // ── Progress tracking ──────────────────────────────────────────

    [Fact]
    public void UpdateProgress_TracksTaskCounts()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        wf.UpdateProgress(total: 10, completed: 3, failed: 1);
        var (total, completed, failed) = wf.GetProgress();

        total.ShouldBe(10);
        completed.ShouldBe(3);
        failed.ShouldBe(1);
    }

    [Fact]
    public void UpdateProgress_MultipleUpdates()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        wf.UpdateProgress(10, 2, 0);
        var (t1, c1, f1) = wf.GetProgress();
        t1.ShouldBe(10);
        c1.ShouldBe(2);

        wf.UpdateProgress(10, 5, 1);
        var (t2, c2, f2) = wf.GetProgress();
        t2.ShouldBe(10);
        c2.ShouldBe(5);
        f2.ShouldBe(1);
    }

    // ── Checkpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckpointAsync_CompletesWithoutError()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        // Should not throw
        await wf.CheckpointAsync(CancellationToken.None);
        wf.CurrentState.ShouldBe(WorkflowState.Running);
    }

    [Fact]
    public async Task CheckpointAsync_WithCancellation()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        using var cts = new CancellationTokenSource();

        // Should not throw even with cancel token
        await wf.CheckpointAsync(cts.Token);
        wf.CurrentState.ShouldBe(WorkflowState.Running);
    }

    // ── Phase timeout ──────────────────────────────────────────────

    [Fact]
    public void HasPhaseTimedOut_WithinTimeout_ReturnsFalse()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan")
        {
            PhaseTimeout = TimeSpan.FromMinutes(5)
        };

        wf.HasPhaseTimedOut().ShouldBeFalse();
    }

    [Fact]
    public void HasPhaseTimedOut_PastTimeout_ReturnsTrue()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan")
        {
            PhaseTimeout = TimeSpan.FromMilliseconds(10)
        };

        System.Threading.Thread.Sleep(20);
        wf.HasPhaseTimedOut().ShouldBeTrue();
    }

    [Fact]
    public void HasPhaseTimedOut_AfterPhaseAdvance_ResetsTimer()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan")
        {
            PhaseTimeout = TimeSpan.FromMilliseconds(10)
        };

        System.Threading.Thread.Sleep(20);
        wf.HasPhaseTimedOut().ShouldBeTrue();

        // Advance phase resets the timer
        wf.AdvancePhase("execute");
        wf.HasPhaseTimedOut().ShouldBeFalse();
    }

    // ── State transitions ──────────────────────────────────────────

    [Fact]
    public void TryTransition_ValidTransition_ReturnsTrue()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");

        var result = wf.TryTransition(WorkflowState.Suspended);

        result.ShouldBeTrue();
        wf.CurrentState.ShouldBe(WorkflowState.Suspended);
    }

    [Fact]
    public void TryTransition_InvalidTransition_ReturnsFalse()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Complete();

        // Cannot transition from Completed
        var result = wf.TryTransition(WorkflowState.Running);

        result.ShouldBeFalse();
        wf.CurrentState.ShouldBe(WorkflowState.Completed);
    }

    [Fact]
    public void TryTransition_SuspendedToRunning_Works()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "tool", "reason", DateTimeOffset.UtcNow));

        var result = wf.TryTransition(WorkflowState.Running);

        result.ShouldBeTrue();
        wf.CurrentState.ShouldBe(WorkflowState.Running);
    }

    [Fact]
    public void TryTransition_SuspendedToAborted_Works()
    {
        var wf = new WorkflowStateMachine("wf-1", "plan");
        wf.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation, "tool", "reason", DateTimeOffset.UtcNow));

        var result = wf.TryTransition(WorkflowState.Aborted);

        result.ShouldBeTrue();
        wf.CurrentState.ShouldBe(WorkflowState.Aborted);
    }
}