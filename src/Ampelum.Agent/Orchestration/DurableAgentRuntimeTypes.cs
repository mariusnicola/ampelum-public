namespace SignalGrid.Ampelum.Agent;

/// <summary>Workflow kind used to classify Ampelum runs.</summary>
public enum DurableWorkflowKind
{
    DeepInit,
    CodeTask,
    Audit,
    Benchmark,
    PortfolioTask,
}

/// <summary>Intent used to start an Ampelum run.</summary>
public sealed record DurableIntent(
    string ProjectId,
    DurableWorkflowKind WorkflowKind,
    string GoalAnchor);

/// <summary>Identifier for a durable Ampelum run.</summary>
public sealed record DurableRunId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Options used to start a durable Ampelum run.</summary>
public sealed record DurableRunOptions
{
    public static DurableRunOptions Default(string workspaceId) => new()
    {
        WorkspaceId = workspaceId,
    };

    public string WorkflowType { get; init; } = "ampelum";
    public string AgentType { get; init; } = "ampelum-agent";
    public string InitialPhase { get; init; } = "intake";
    public string? ProjectId { get; init; }
    public string? WorkspaceId { get; init; }
    public bool IsAutonomous { get; init; } = true;
}

/// <summary>Input used to resume a paused Ampelum run.</summary>
public sealed record DurableResumeInput(
    string ResumeToken,
    string Reason,
    string? NextPhase = null);

/// <summary>Durable clarification or approval request.</summary>
public sealed record DurablePendingInterrupt(
    string Prompt,
    string ResumeToken,
    IReadOnlyList<string> Options,
    DateTimeOffset ExpiresAt,
    string? NextPhase = null);

/// <summary>Immutable durable snapshot for an Ampelum run.</summary>
public sealed record DurableRunSnapshot
{
    public required DurableRunId RunId { get; init; }
    public required string ProjectId { get; init; }
    public required DurableWorkflowKind WorkflowKind { get; init; }
    public required string Goal { get; init; }
    public string GoalAnchor => Goal;
    public required string WorkflowType { get; init; }
    public required string AgentType { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string? WorkspaceId { get; init; }
    public required WorkflowState State { get; init; }
    public required string CurrentPhase { get; init; }
    public required PhaseCheckpoint Checkpoint { get; init; }
    public required WorkflowExecution Execution { get; init; }
    public IReadOnlyList<PhaseContextMessage> PhaseContext { get; init; } = [];
    public IReadOnlyList<DurableRunEvent> RecentEvents { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public DurablePendingInterrupt? PendingInterrupt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Reason { get; init; }

    public bool IsTerminal =>
        State is WorkflowState.Completed or WorkflowState.Failed or WorkflowState.Aborted;
}

/// <summary>Snapshot change event emitted by the runtime stream.</summary>
public sealed record DurableRunEvent(
    DurableRunId RunId,
    DurableRunEventKind Kind,
    string Message,
    DurableRunSnapshot Snapshot,
    DateTimeOffset Timestamp);

public enum DurableRunEventKind
{
    Started,
    Updated,
    CheckpointSaved,
    InterruptRequested,
    Resumed,
    Paused,
    Cancelled,
    Completed,
    Failed,
    Aborted,
}

/// <summary>Durable lifecycle API for Ampelum runs.</summary>
public interface IDurableAgentRuntime
{
    Task<DurableRunId> StartAsync(DurableIntent intent, DurableRunOptions options, CancellationToken ct = default);
    Task PauseAsync(DurableRunId runId, string reason, CancellationToken ct = default);
    Task ResumeAsync(DurableRunId runId, DurableResumeInput input, CancellationToken ct = default);
    Task CancelAsync(DurableRunId runId, CancellationToken ct = default);
    Task<DurableRunSnapshot?> GetSnapshotAsync(DurableRunId runId, CancellationToken ct = default);
    IAsyncEnumerable<DurableRunEvent> StreamAsync(DurableRunId runId, CancellationToken ct = default);
}
