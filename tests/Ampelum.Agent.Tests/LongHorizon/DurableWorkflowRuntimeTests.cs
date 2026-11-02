using System.Text.Json;
using SignalGrid.Ampelum.Agent;
using Shouldly;
using Xunit;

namespace Ampelum.Agent.Tests.LongHorizon;

[Collection("LongHorizon")]
public sealed class DurableWorkflowRuntimeTests : IAsyncDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ampelum-runtime-{Guid.NewGuid():N}");

    public DurableWorkflowRuntimeTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Start_PersistsCheckpoint_AndSnapshotReloads()
    {
        var store = new JsonFileStateStore(_tempDirectory);
        var checkpoint = CreateCheckpoint(
            workflowId: "run-1",
            phase: "intake",
            goalAnchor: "build the feature",
            checkpointId: "cp-start",
            activeContext: "collect requirements");

        await store.SaveAsync(checkpoint, TestContext.Current.CancellationToken);

        var snapshot = await store.LoadAsync("run-1", TestContext.Current.CancellationToken);

        snapshot.ShouldNotBeNull();
        snapshot!.CheckpointId.ShouldBe("cp-start");
        snapshot.WorkflowId.ShouldBe("run-1");
        snapshot.Phase.ShouldBe("intake");
        snapshot.GoalAnchor.ShouldBe("build the feature");
        snapshot.ActiveContext.ShouldBe("collect requirements");
    }

    [Fact]
    public async Task Resume_LoadsLatestDurableCheckpoint_FromFreshStoreInstance()
    {
        var store = new JsonFileStateStore(_tempDirectory);
        await store.SaveAsync(CreateCheckpoint(
            workflowId: "run-2",
            phase: "intake",
            goalAnchor: "ship the thing",
            checkpointId: "cp-1"), TestContext.Current.CancellationToken);

        await store.SaveAsync(CreateCheckpoint(
            workflowId: "run-2",
            phase: "plan",
            goalAnchor: "ship the thing",
            checkpointId: "cp-2",
            previousCheckpointId: "cp-1",
            resumedFrom: "agent-1",
            activeContext: "compile plan"), TestContext.Current.CancellationToken);

        var reloaded = new JsonFileStateStore(_tempDirectory);
        var resumed = await reloaded.LoadAsync("run-2", TestContext.Current.CancellationToken);

        resumed.ShouldNotBeNull();
        resumed!.CheckpointId.ShouldBe("cp-2");
        resumed.PreviousCheckpointId.ShouldBe("cp-1");
        resumed.ResumedFrom.ShouldBe("agent-1");
        resumed.Phase.ShouldBe("plan");
        resumed.ActiveContext.ShouldBe("compile plan");
    }

    [Fact]
    public void PauseAndCancel_UpdateWorkflowSnapshotState()
    {
        var pausedMachine = new WorkflowStateMachine("run-3", "clarify");
        pausedMachine.Escalate(new EscalationContext(
            EscalationType.RequiresConfirmation,
            "write_file",
            "Need approval before writing files",
            DateTimeOffset.UtcNow));

        var paused = new WorkflowExecution(
            pausedMachine.WorkflowId,
            "sdlc",
            "orchestrator",
            "instance-1",
            pausedMachine.CurrentState,
            pausedMachine.CurrentPhase,
            pausedMachine.StartedAt.UtcDateTime,
            DateTime.UtcNow,
            pausedMachine.PhaseTimeout,
            1,
            0,
            0,
            CheckpointId: "cp-paused");

        var pausedRoundTrip = JsonSerializer.Deserialize<WorkflowExecution>(
            JsonSerializer.Serialize(paused))!;
        pausedRoundTrip.CurrentState.ShouldBe(WorkflowState.Suspended);
        pausedRoundTrip.CurrentPhase.ShouldBe("clarify");
        pausedRoundTrip.CheckpointId.ShouldBe("cp-paused");

        pausedMachine.Abort();

        var cancelled = paused with { CurrentState = pausedMachine.CurrentState };
        var cancelledRoundTrip = JsonSerializer.Deserialize<WorkflowExecution>(
            JsonSerializer.Serialize(cancelled))!;
        cancelledRoundTrip.CurrentState.ShouldBe(WorkflowState.Aborted);
        cancelledRoundTrip.CurrentPhase.ShouldBe("clarify");
        cancelledRoundTrip.CheckpointId.ShouldBe("cp-paused");
    }

    [Fact]
    public async Task Stream_AndSnapshot_ReturnPersistedData()
    {
        var store = new JsonFileStateStore(_tempDirectory);
        await store.SaveAsync(CreateCheckpoint(
            workflowId: "run-4",
            phase: "intake",
            goalAnchor: "keep state durable",
            checkpointId: "cp-1"), TestContext.Current.CancellationToken);

        await store.SaveAsync(CreateCheckpoint(
            workflowId: "run-4",
            phase: "execute",
            goalAnchor: "keep state durable",
            checkpointId: "cp-2",
            previousCheckpointId: "cp-1",
            activeContext: "tool output"), TestContext.Current.CancellationToken);

        await store.SaveAsync(CreateCheckpoint(
            workflowId: "run-4",
            phase: "review",
            goalAnchor: "keep state durable",
            checkpointId: "cp-3",
            previousCheckpointId: "cp-2",
            activeContext: "evidence bundle"), TestContext.Current.CancellationToken);

        var reloaded = new JsonFileStateStore(_tempDirectory);

        var stream = await reloaded.ListAsync("run-4", TestContext.Current.CancellationToken);
        stream.Count.ShouldBe(3);
        stream.Select(c => c.CheckpointId).ShouldContain("cp-1");
        stream.Select(c => c.CheckpointId).ShouldContain("cp-2");
        stream.Select(c => c.CheckpointId).ShouldContain("cp-3");

        var snapshot = await reloaded.LoadAsync("run-4", TestContext.Current.CancellationToken);
        snapshot.ShouldNotBeNull();
        snapshot!.CheckpointId.ShouldBe("cp-3");
        snapshot.Phase.ShouldBe("review");
        snapshot.ActiveContext.ShouldBe("evidence bundle");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }

        await ValueTask.CompletedTask;
    }

    private static PhaseCheckpoint CreateCheckpoint(
        string workflowId,
        string phase,
        string goalAnchor,
        string checkpointId,
        string? previousCheckpointId = null,
        string? resumedFrom = null,
        string? activeContext = null,
        string? workspaceId = null)
    {
        return new PhaseCheckpoint
        {
            CheckpointId = checkpointId,
            WorkflowId = workflowId,
            Phase = phase,
            GoalAnchor = goalAnchor,
            PreviousCheckpointId = previousCheckpointId,
            ResumedFrom = resumedFrom,
            ActiveContext = activeContext,
            WorkspaceId = workspaceId,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }
}
