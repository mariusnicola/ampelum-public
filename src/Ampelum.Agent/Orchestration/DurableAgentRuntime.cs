using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agent;

public sealed class DurableAgentRuntime : IDurableAgentRuntime
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    readonly IStateStore _stateStore;
    readonly IDurableRunEventStore _eventStore;
    readonly MemoryManager _memoryManager;
    readonly ILogger _logger;
    readonly ConcurrentDictionary<string, RunSession> _sessions = new(StringComparer.Ordinal);

    public DurableAgentRuntime(
        IStateStore stateStore,
        IDurableRunEventStore eventStore,
        ILogger? logger = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _memoryManager = new MemoryManager(stateStore);
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<DurableRunId> StartAsync(DurableIntent intent, DurableRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(intent.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(intent));
        if (string.IsNullOrWhiteSpace(intent.GoalAnchor))
            throw new ArgumentException("GoalAnchor is required.", nameof(intent));

        var runId = new DurableRunId(Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var normalizedOptions = options with
        {
            ProjectId = options.ProjectId ?? intent.ProjectId,
        };

        var envelope = CreateEnvelope(intent, normalizedOptions, runId.Value, now);
        var checkpoint = CreateCheckpoint(runId.Value, envelope, phaseIndex: 0, previousCheckpointId: null, now);
        var snapshot = BuildSnapshot(runId, checkpoint, envelope, [], null);
        var session = new RunSession(snapshot, envelope);

        if (!_sessions.TryAdd(runId.Value, session))
            throw new InvalidOperationException($"Run '{runId}' already exists.");

        await _stateStore.SaveAsync(checkpoint, ct).ConfigureAwait(false);
        await EmitAsync(session, DurableRunEventKind.Started, "Run started", ct).ConfigureAwait(false);
        await EmitAsync(session, DurableRunEventKind.CheckpointSaved, "Initial checkpoint saved", ct).ConfigureAwait(false);
        _logger.LogDebug("Started Ampelum run {RunId}", runId.Value);
        return runId;
    }

    public async Task PauseAsync(DurableRunId runId, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Pause reason is required.", nameof(reason));

        var session = await GetOrLoadAsync(runId, ct).ConfigureAwait(false);
        if (session.Snapshot.State is WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Aborted)
            throw new InvalidOperationException($"Run '{runId}' is already terminal.");
        if (session.Snapshot.State != WorkflowState.Running)
            throw new InvalidOperationException($"Run '{runId}' is not running.");

        var interrupt = CreateInterrupt(session.Snapshot, reason);
        await PersistAsync(
            session,
            WorkflowState.Suspended,
            session.Snapshot.CurrentPhase,
            reason,
            interrupt,
            ct).ConfigureAwait(false);

        await EmitAsync(session, DurableRunEventKind.InterruptRequested, reason, ct).ConfigureAwait(false);
        await EmitAsync(session, DurableRunEventKind.CheckpointSaved, "Pause checkpoint saved", ct).ConfigureAwait(false);
        _logger.LogDebug("Paused Ampelum run {RunId}", runId.Value);
    }

    public async Task ResumeAsync(DurableRunId runId, DurableResumeInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ResumeToken))
            throw new ArgumentException("Resume token is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new ArgumentException("Resume reason is required.", nameof(input));

        var session = await GetOrLoadAsync(runId, ct).ConfigureAwait(false);
        if (session.Snapshot.PendingInterrupt is null)
            throw new InvalidOperationException($"Run '{runId}' has no pending interrupt.");
        if (!string.Equals(session.Snapshot.PendingInterrupt.ResumeToken, input.ResumeToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Resume token does not match the pending interrupt.");

        var nextPhase = string.IsNullOrWhiteSpace(input.NextPhase)
            ? session.Snapshot.PendingInterrupt.NextPhase ?? session.Snapshot.CurrentPhase
            : input.NextPhase;

        session.Machine.ResolveEscalation();
        session.Machine.AdvancePhase(nextPhase);

        await PersistAsync(
            session,
            WorkflowState.Running,
            nextPhase,
            input.Reason,
            pendingInterrupt: null,
            ct).ConfigureAwait(false);

        await EmitAsync(session, DurableRunEventKind.Resumed, input.Reason, ct).ConfigureAwait(false);
        await EmitAsync(session, DurableRunEventKind.CheckpointSaved, "Resume checkpoint saved", ct).ConfigureAwait(false);
        _logger.LogDebug("Resumed Ampelum run {RunId} into phase {Phase}", runId.Value, nextPhase);
    }

    public async Task CancelAsync(DurableRunId runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var session = await GetOrLoadAsync(runId, ct).ConfigureAwait(false);
        if (session.Snapshot.State is WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Aborted)
            throw new InvalidOperationException($"Run '{runId}' is already terminal.");

        session.Machine.Abort();

        await PersistAsync(
            session,
            WorkflowState.Aborted,
            session.Snapshot.CurrentPhase,
            reason: null,
            pendingInterrupt: null,
            ct).ConfigureAwait(false);

        await EmitAsync(session, DurableRunEventKind.Cancelled, "Run cancelled", ct).ConfigureAwait(false);
        await EmitAsync(session, DurableRunEventKind.CheckpointSaved, "Cancel checkpoint saved", ct).ConfigureAwait(false);
        session.Complete();
        _logger.LogDebug("Cancelled Ampelum run {RunId}", runId.Value);
    }

    public async Task<DurableRunSnapshot?> GetSnapshotAsync(DurableRunId runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (_sessions.TryGetValue(runId.Value, out var session))
            return await RefreshSnapshotAsync(session, ct).ConfigureAwait(false);

        var checkpoint = await _stateStore.LoadAsync(runId.Value, ct).ConfigureAwait(false);
        if (checkpoint is null)
            return null;

        var loaded = await CreateSessionAsync(runId, checkpoint, ct).ConfigureAwait(false);
        _sessions.TryAdd(runId.Value, loaded);
        return loaded.Snapshot;
    }

    public async IAsyncEnumerable<DurableRunEvent> StreamAsync(DurableRunId runId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var replay = await _eventStore.ReadAsync(runId, ct).ConfigureAwait(false);
        foreach (var evt in replay)
            yield return evt;

        if (!_sessions.TryGetValue(runId.Value, out var session))
            yield break;

        await foreach (var evt in session.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    async Task<RunSession> GetOrLoadAsync(DurableRunId runId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(runId.Value, out var existing))
            return existing;

        var checkpoint = await _stateStore.LoadAsync(runId.Value, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{runId}' has no durable checkpoint.");

        var session = await CreateSessionAsync(runId, checkpoint, ct).ConfigureAwait(false);
        _sessions.TryAdd(runId.Value, session);
        return session;
    }

    async Task<RunSession> CreateSessionAsync(DurableRunId runId, PhaseCheckpoint checkpoint, CancellationToken ct)
    {
        var envelope = DeserializeEnvelope(checkpoint);
        var recentEvents = await _eventStore.ReadAsync(runId, ct).ConfigureAwait(false);
        var snapshot = BuildSnapshot(runId, checkpoint, envelope, recentEvents, envelope.PendingInterrupt);
        return new RunSession(snapshot, envelope);
    }

    RunEnvelope DeserializeEnvelope(PhaseCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.ActiveContext))
            throw new InvalidOperationException($"Checkpoint '{checkpoint.CheckpointId ?? checkpoint.WorkflowId}' is missing runtime context.");

        return JsonSerializer.Deserialize<RunEnvelope>(checkpoint.ActiveContext, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize runtime envelope.");
    }

    RunEnvelope CreateEnvelope(
        DurableIntent intent,
        DurableRunOptions options,
        string workflowId,
        DateTimeOffset now)
    {
        var execution = new WorkflowExecution(
            WorkflowId: workflowId,
            WorkflowType: options.WorkflowType,
            AgentType: options.AgentType,
            AgentInstanceId: Guid.NewGuid().ToString("N"),
            CurrentState: WorkflowState.Running,
            CurrentPhase: options.InitialPhase,
            StartedAt: now.UtcDateTime,
            PhaseStartedAt: now.UtcDateTime,
            PhaseTimeout: TimeSpan.FromMinutes(5),
            TotalTasks: 0,
            CompletedTasks: 0,
            FailedTasks: 0,
            CheckpointId: null,
            Escalation: null,
            Metadata: null);

        return new RunEnvelope(
            Intent: intent,
            Options: options,
            Execution: execution,
            PendingInterrupt: null,
            UpdatedAt: now,
            Reason: null,
            Evidence: []);
    }

    DurableRunSnapshot BuildSnapshot(
        DurableRunId runId,
        PhaseCheckpoint checkpoint,
        RunEnvelope envelope,
        IReadOnlyList<DurableRunEvent> recentEvents,
        DurablePendingInterrupt? pendingInterrupt)
    {
        var phaseContext = _memoryManager.BuildPhaseContext(checkpoint);
        return new DurableRunSnapshot
        {
            RunId = runId,
            ProjectId = envelope.Intent.ProjectId,
            WorkflowKind = envelope.Intent.WorkflowKind,
            Goal = envelope.Intent.GoalAnchor,
            WorkflowType = envelope.Options.WorkflowType,
            AgentType = envelope.Options.AgentType,
            AgentInstanceId = envelope.Execution.AgentInstanceId,
            WorkspaceId = envelope.Options.WorkspaceId,
            State = envelope.Execution.CurrentState,
            CurrentPhase = envelope.Execution.CurrentPhase,
            Checkpoint = checkpoint,
            Execution = envelope.Execution,
            PhaseContext = phaseContext,
            RecentEvents = recentEvents,
            PendingInterrupt = pendingInterrupt,
            UpdatedAt = envelope.UpdatedAt,
            Reason = envelope.Reason,
            Evidence = envelope.Evidence,
        };
    }

    DurablePendingInterrupt CreateInterrupt(DurableRunSnapshot snapshot, string reason)
    {
        var token = Guid.NewGuid().ToString("N");
        return new DurablePendingInterrupt(
            Prompt: reason,
            ResumeToken: token,
            Options: ["Resume", "Cancel"],
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            NextPhase: snapshot.CurrentPhase);
    }

    async Task<DurableRunSnapshot> PersistAsync(
        RunSession session,
        WorkflowState state,
        string phase,
        string? reason,
        DurablePendingInterrupt? pendingInterrupt,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var previousCheckpoint = session.Snapshot.Checkpoint;
        var phaseIndex = string.Equals(previousCheckpoint.Phase, phase, StringComparison.Ordinal)
            ? previousCheckpoint.PhaseIndex
            : previousCheckpoint.PhaseIndex + 1;

        var execution = session.Envelope.Execution with
        {
            CurrentState = state,
            CurrentPhase = phase,
        };

        var envelope = session.Envelope with
        {
            Execution = execution,
            PendingInterrupt = pendingInterrupt,
            UpdatedAt = now,
            Reason = reason,
        };

        var checkpoint = CreateCheckpoint(
            session.RunId.Value,
            envelope,
            phaseIndex,
            previousCheckpoint.CheckpointId,
            now);

        await _stateStore.SaveAsync(checkpoint, ct).ConfigureAwait(false);

        session.Envelope = envelope;
        session.RecentEvents = session.RecentEvents.ToList();
        session.Snapshot = BuildSnapshot(
            session.RunId,
            checkpoint,
            envelope,
            session.RecentEvents,
            pendingInterrupt);
        return session.Snapshot;
    }

    async Task<DurableRunSnapshot> RefreshSnapshotAsync(RunSession session, CancellationToken ct)
    {
        var recentEvents = await _eventStore.ReadAsync(session.RunId, ct).ConfigureAwait(false);
        var checkpoint = await _stateStore.LoadAsync(session.RunId.Value, ct).ConfigureAwait(false);
        if (checkpoint is null)
            return session.Snapshot;

        var envelope = DeserializeEnvelope(checkpoint);
        var snapshot = BuildSnapshot(session.RunId, checkpoint, envelope, recentEvents, envelope.PendingInterrupt);
        session.Snapshot = snapshot;
        session.Envelope = envelope;
        session.RecentEvents = recentEvents.ToList();
        return snapshot;
    }

    async Task EmitAsync(RunSession session, DurableRunEventKind kind, string message, CancellationToken ct)
    {
        var snapshot = session.Snapshot with
        {
            RecentEvents = [],
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var evt = new DurableRunEvent(
            session.RunId,
            kind,
            message,
            snapshot,
            snapshot.UpdatedAt);

        await _eventStore.AppendAsync(evt, ct).ConfigureAwait(false);
        session.RecentEvents.Add(evt);
        session.Writer.TryWrite(evt);
        session.Snapshot = session.Snapshot with
        {
            RecentEvents = session.RecentEvents.ToList(),
            UpdatedAt = evt.Timestamp,
        };
    }

    PhaseCheckpoint CreateCheckpoint(
        string workflowId,
        RunEnvelope envelope,
        int phaseIndex,
        string? previousCheckpointId,
        DateTimeOffset capturedAt)
    {
        return new PhaseCheckpoint
        {
            CheckpointId = Guid.NewGuid().ToString("N"),
            WorkflowId = workflowId,
            AgentInstanceId = envelope.Execution.AgentInstanceId,
            Phase = envelope.Execution.CurrentPhase,
            PhaseIndex = phaseIndex,
            GoalAnchor = envelope.Intent.GoalAnchor,
            CompletedPhases = [],
            ActiveContext = JsonSerializer.Serialize(envelope, JsonOptions),
            CapturedAt = capturedAt,
            ResumedFrom = null,
            PreviousCheckpointId = previousCheckpointId,
            WorkspaceId = envelope.Options.WorkspaceId,
        };
    }

    sealed class RunSession
    {
        readonly Channel<DurableRunEvent> _channel = Channel.CreateUnbounded<DurableRunEvent>();

        public RunSession(DurableRunSnapshot snapshot, RunEnvelope envelope)
        {
            Snapshot = snapshot;
            Envelope = envelope;
            RecentEvents = snapshot.RecentEvents.ToList();
            Machine = new WorkflowStateMachine(snapshot.RunId.Value, snapshot.CurrentPhase, snapshot.State == WorkflowState.Running);
            RestoreMachine(snapshot);
        }

        public DurableRunId RunId => Snapshot.RunId;
        public DurableRunSnapshot Snapshot { get; set; }
        public RunEnvelope Envelope { get; set; }
        public List<DurableRunEvent> RecentEvents { get; set; }
        public WorkflowStateMachine Machine { get; }
        public ChannelReader<DurableRunEvent> Reader => _channel.Reader;
        public ChannelWriter<DurableRunEvent> Writer => _channel.Writer;

        public void Complete() => Writer.TryComplete();

        void RestoreMachine(DurableRunSnapshot snapshot)
        {
            switch (snapshot.State)
            {
                case WorkflowState.Running:
                    break;
                case WorkflowState.Suspended:
                    Machine.Escalate(new EscalationContext(
                        EscalationType.RequiresConfirmation,
                        "resume",
                        snapshot.PendingInterrupt?.Prompt ?? snapshot.Reason ?? "paused",
                        snapshot.UpdatedAt,
                        null));
                    break;
                case WorkflowState.Completed:
                    Machine.Complete();
                    break;
                case WorkflowState.Failed:
                    Machine.Fail();
                    break;
                case WorkflowState.Aborted:
                    Machine.Abort();
                    break;
            }
        }
    }

    internal sealed record RunEnvelope(
        DurableIntent Intent,
        DurableRunOptions Options,
        WorkflowExecution Execution,
        DurablePendingInterrupt? PendingInterrupt,
        DateTimeOffset UpdatedAt,
        string? Reason,
        IReadOnlyList<string> Evidence);
}
