using SignalGrid.Ampelum.Agent;
using Shouldly;
using Xunit;

namespace Ampelum.Agent.Tests.LongHorizon;

[Collection("LongHorizon")]
public sealed class DurableAgentRuntimeTests : IDisposable
{
    readonly string _root;
    readonly IStateStore _stateStore;
    readonly IDurableRunEventStore _eventStore;

    public DurableAgentRuntimeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ampelum-runtime-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _stateStore = new JsonFileStateStore(Path.Combine(_root, "checkpoints"));
        _eventStore = new JsonFileDurableRunEventStore(Path.Combine(_root, "events"));
    }

    [Fact]
    public async Task StartAsync_Writes_Durable_Snapshot()
    {
        var runtime = new DurableAgentRuntime(_stateStore, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-1", DurableWorkflowKind.DeepInit, "ship the feature"),
            DurableRunOptions.Default("ws-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await runtime.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);

        snapshot.ShouldNotBeNull();
        snapshot!.RunId.ShouldBe(runId);
        snapshot.ProjectId.ShouldBe("proj-1");
        snapshot.WorkflowKind.ShouldBe(DurableWorkflowKind.DeepInit);
        snapshot.Goal.ShouldBe("ship the feature");
        snapshot.State.ShouldBe(WorkflowState.Running);
        snapshot.CurrentPhase.ShouldBe("intake");
        snapshot.PendingInterrupt.ShouldBeNull();
        snapshot.Checkpoint.ActiveContext.ShouldNotBeNullOrWhiteSpace();
        snapshot.RecentEvents.Any(e => e.Kind == DurableRunEventKind.Started).ShouldBeTrue();
        snapshot.RecentEvents.Any(e => e.Kind == DurableRunEventKind.CheckpointSaved).ShouldBeTrue();

        var restored = new DurableAgentRuntime(_stateStore, _eventStore);
        var restoredSnapshot = await restored.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);

        restoredSnapshot.ShouldNotBeNull();
        restoredSnapshot!.RunId.ShouldBe(runId);
        restoredSnapshot.State.ShouldBe(WorkflowState.Running);
        restoredSnapshot.GoalAnchor.ShouldBe("ship the feature");
        restoredSnapshot.Checkpoint.ActiveContext.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Pause_Resume_RoundTrips_Through_Durable_State()
    {
        var runtime = new DurableAgentRuntime(_stateStore, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-2", DurableWorkflowKind.CodeTask, "update the parser"),
            DurableRunOptions.Default("ws-2"),
            TestContext.Current.CancellationToken);

        await runtime.PauseAsync(runId, "Need approval for file writes", TestContext.Current.CancellationToken);

        var paused = await runtime.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);
        paused.ShouldNotBeNull();
        paused!.State.ShouldBe(WorkflowState.Suspended);
        paused.PendingInterrupt.ShouldNotBeNull();
        paused.PendingInterrupt!.Prompt.ShouldBe("Need approval for file writes");
        paused.PendingInterrupt.ResumeToken.ShouldNotBeNullOrWhiteSpace();
        paused.RecentEvents.Any(e => e.Kind == DurableRunEventKind.InterruptRequested).ShouldBeTrue();

        var restored = new DurableAgentRuntime(_stateStore, _eventStore);
        var resumedView = await restored.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);
        resumedView.ShouldNotBeNull();
        resumedView!.State.ShouldBe(WorkflowState.Suspended);
        resumedView.PendingInterrupt.ShouldNotBeNull();

        await restored.ResumeAsync(
            runId,
            new DurableResumeInput(resumedView.PendingInterrupt!.ResumeToken, "approved", "execute"),
            TestContext.Current.CancellationToken);

        var running = await restored.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);
        running.ShouldNotBeNull();
        running!.State.ShouldBe(WorkflowState.Running);
        running.PendingInterrupt.ShouldBeNull();
        running.CurrentPhase.ShouldBe("execute");
        running.Reason.ShouldBe("approved");
        running.RecentEvents.Any(e => e.Kind == DurableRunEventKind.Resumed).ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAsync_Persists_Aborted_State()
    {
        var runtime = new DurableAgentRuntime(_stateStore, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-3", DurableWorkflowKind.Audit, "audit the repo"),
            DurableRunOptions.Default("ws-3"),
            TestContext.Current.CancellationToken);

        await runtime.CancelAsync(runId, TestContext.Current.CancellationToken);

        var snapshot = await runtime.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);
        snapshot.ShouldNotBeNull();
        snapshot!.State.ShouldBe(WorkflowState.Aborted);
        snapshot.IsTerminal.ShouldBeTrue();
        snapshot.RecentEvents.Any(e => e.Kind == DurableRunEventKind.Cancelled).ShouldBeTrue();

        var restored = new DurableAgentRuntime(_stateStore, _eventStore);
        var replayed = await restored.GetSnapshotAsync(runId, TestContext.Current.CancellationToken);
        replayed.ShouldNotBeNull();
        replayed!.State.ShouldBe(WorkflowState.Aborted);
    }

    [Fact]
    public async Task StreamAsync_Replays_Persisted_Events_After_Restart()
    {
        var runtime = new DurableAgentRuntime(_stateStore, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-4", DurableWorkflowKind.Benchmark, "run the benchmark"),
            DurableRunOptions.Default("ws-4"),
            TestContext.Current.CancellationToken);

        await runtime.PauseAsync(runId, "Checkpoint before benchmark", TestContext.Current.CancellationToken);

        var restored = new DurableAgentRuntime(_stateStore, _eventStore);
        var events = new List<DurableRunEvent>();
        await foreach (var evt in restored.StreamAsync(runId, TestContext.Current.CancellationToken))
            events.Add(evt);

        events.Count.ShouldBe(4);
        events[0].Kind.ShouldBe(DurableRunEventKind.Started);
        events[1].Kind.ShouldBe(DurableRunEventKind.CheckpointSaved);
        events[2].Kind.ShouldBe(DurableRunEventKind.InterruptRequested);
        events[3].Kind.ShouldBe(DurableRunEventKind.CheckpointSaved);
        events[2].Snapshot.State.ShouldBe(WorkflowState.Suspended);
        events[2].Snapshot.PendingInterrupt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Pause_ThenResume_OnTheSameInstance_ReturnsToRunning_AndCanPauseAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new DurableAgentRuntime(_stateStore, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-5", DurableWorkflowKind.CodeTask, "update the parser"),
            DurableRunOptions.Default("ws-5"),
            ct);

        for (var round = 1; round <= 2; round++)
        {
            await runtime.PauseAsync(runId, $"approval {round}", ct);
            var paused = await runtime.GetSnapshotAsync(runId, ct);
            paused!.State.ShouldBe(WorkflowState.Suspended);

            await runtime.ResumeAsync(runId, new DurableResumeInput(paused.PendingInterrupt!.ResumeToken, "approved", "execute"), ct);

            var running = await runtime.GetSnapshotAsync(runId, ct);
            running!.State.ShouldBe(WorkflowState.Running);
            running.PendingInterrupt.ShouldBeNull();
        }
    }

    sealed class FailingOnceStore(IStateStore inner) : IStateStore
    {
        public bool FailNextSave { get; set; }

        public Task SaveAsync(PhaseCheckpoint checkpoint, CancellationToken ct = default)
        {
            if (!FailNextSave) return inner.SaveAsync(checkpoint, ct);
            FailNextSave = false;
            throw new IOException("disk full");
        }

        public Task<PhaseCheckpoint?> LoadAsync(string workflowId, CancellationToken ct = default) => inner.LoadAsync(workflowId, ct);
        public Task<PhaseCheckpoint?> LoadAsync(string workflowId, string? workspaceId, CancellationToken ct = default) => inner.LoadAsync(workflowId, workspaceId, ct);
        public Task<PhaseCheckpoint?> LoadByIdAsync(string checkpointId, CancellationToken ct = default) => inner.LoadByIdAsync(checkpointId, ct);
        public Task<IReadOnlyList<PhaseCheckpoint>> ListAsync(string workflowId, CancellationToken ct = default) => inner.ListAsync(workflowId, ct);
        public Task PurgeAsync(string workflowId, CancellationToken ct = default) => inner.PurgeAsync(workflowId, ct);
    }

    [Fact]
    public async Task Resume_WhenTheStoreFailsOnce_ThrowsAndARetryOnTheSameInstanceSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FailingOnceStore(_stateStore);
        var runtime = new DurableAgentRuntime(store, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-6", DurableWorkflowKind.CodeTask, "update the parser"),
            DurableRunOptions.Default("ws-6"),
            ct);
        await runtime.PauseAsync(runId, "approval", ct);
        var token = (await runtime.GetSnapshotAsync(runId, ct))!.PendingInterrupt!.ResumeToken;
        var input = new DurableResumeInput(token, "approved", "execute");

        store.FailNextSave = true;
        await Should.ThrowAsync<IOException>(() => runtime.ResumeAsync(runId, input, ct));
        (await runtime.GetSnapshotAsync(runId, ct))!.State.ShouldBe(WorkflowState.Suspended);

        await runtime.ResumeAsync(runId, input, ct);

        var resumed = await runtime.GetSnapshotAsync(runId, ct);
        resumed!.State.ShouldBe(WorkflowState.Running);
        resumed.PendingInterrupt.ShouldBeNull();
    }

    [Fact]
    public async Task Pause_WhenTheStoreFailsOnce_ThrowsAndARetryOnTheSameInstanceSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FailingOnceStore(_stateStore);
        var runtime = new DurableAgentRuntime(store, _eventStore);
        var runId = await runtime.StartAsync(
            new DurableIntent("proj-7", DurableWorkflowKind.CodeTask, "update the parser"),
            DurableRunOptions.Default("ws-7"),
            ct);

        store.FailNextSave = true;
        await Should.ThrowAsync<IOException>(() => runtime.PauseAsync(runId, "approval", ct));
        (await runtime.GetSnapshotAsync(runId, ct))!.State.ShouldBe(WorkflowState.Running);

        await runtime.PauseAsync(runId, "approval", ct);

        var paused = await runtime.GetSnapshotAsync(runId, ct);
        paused!.State.ShouldBe(WorkflowState.Suspended);
        await runtime.ResumeAsync(runId, new DurableResumeInput(paused.PendingInterrupt!.ResumeToken, "ok", "execute"), ct);
        (await runtime.GetSnapshotAsync(runId, ct))!.State.ShouldBe(WorkflowState.Running);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
