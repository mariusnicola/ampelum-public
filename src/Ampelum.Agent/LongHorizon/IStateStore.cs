namespace SignalGrid.Ampelum.Agent;

/// <summary>Persists phase checkpoints for crash recovery and long-horizon runs.
/// File-based and in-memory backends required.</summary>
public interface IStateStore
{
    /// <summary>Save a checkpoint. Overwrites if same phase.</summary>
    Task SaveAsync(PhaseCheckpoint checkpoint, CancellationToken ct = default);

    /// <summary>Load the latest checkpoint for a workflow.</summary>
    Task<PhaseCheckpoint?> LoadAsync(string workflowId, CancellationToken ct = default);

    /// <summary>Load the latest checkpoint for a workflow, filtered by workspace partition.
    /// Pass workspaceId = null for unpartitioned checkpoints (backwards-compatible).</summary>
    Task<PhaseCheckpoint?> LoadAsync(
        string workflowId,
        string? workspaceId,
        CancellationToken ct = default);

    /// <summary>Load a specific checkpoint by its CheckpointId.</summary>
    Task<PhaseCheckpoint?> LoadByIdAsync(string checkpointId, CancellationToken ct = default);

    /// <summary>List all checkpoints for a workflow (sorted newest first).</summary>
    Task<IReadOnlyList<PhaseCheckpoint>> ListAsync(string workflowId, CancellationToken ct = default);

    /// <summary>Delete all checkpoints for a workflow (cleanup after completion).</summary>
    Task PurgeAsync(string workflowId, CancellationToken ct = default);
}

/// <summary>Lightweight view of a checkpoint for listing operations.</summary>
public sealed record PhaseCheckpointSummary(
    string CheckpointId,
    string WorkflowId,
    string Phase,
    DateTimeOffset CapturedAt,
    string? AgentInstanceId
);

/// <summary>A checkpoint captured at a phase transition.
/// GoalAnchor is NEVER dropped. CompletedPhases are structured summaries.
/// ActiveContext is archived at phase transitions.</summary>
public sealed record PhaseCheckpoint
{
    /// <summary>Unique identifier for this checkpoint (auto-generated GUID).</summary>
    public string? CheckpointId { get; init; } = null;

    /// <summary>Workflow identifier.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Links to AgentIdentity.InstanceId if known.</summary>
    public string? AgentInstanceId { get; init; } = null;

    /// <summary>Current phase name (string — no enum dependency).</summary>
    public required string Phase { get; init; }

    /// <summary>Zero-based index of this phase in the workflow.</summary>
    public int PhaseIndex { get; init; } = 0;

    /// <summary>The goal anchor — never dropped, re-injected at every phase.</summary>
    public required string GoalAnchor { get; init; }

    /// <summary>Structured summaries of completed phases. No prose fields.</summary>
    public IReadOnlyList<PhaseSummary> CompletedPhases { get; init; } = [];

    /// <summary>Active context (raw working data). Archived on phase transition.</summary>
    public string? ActiveContext { get; init; }

    /// <summary>When this checkpoint was created.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Previous InstanceId if this is a crash recovery resume.</summary>
    public string? ResumedFrom { get; init; }

    /// <summary>Previous checkpoint id — for audit chain.</summary>
    public string? PreviousCheckpointId { get; init; } = null;

    /// <summary>Workspace partition identifier. Null for checkpoints created before Phase 22.
    /// Additive — no migration needed, null is backwards-compatible.</summary>
    public string? WorkspaceId { get; init; }
}

/// <summary>Structured summary of a completed phase. No prose — structured data only.</summary>
public sealed record PhaseSummary
{
    public required string PhaseName { get; init; }
    public required bool Succeeded { get; init; }
    public int TasksCompleted { get; init; }
    public int TasksFailed { get; init; }
    public long TokensUsed { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>A single entry in the active phase context (tool calls, decisions, results).</summary>
public sealed record PhaseContextEntry(
    string EntryType,       // "tool_call", "tool_result", "decision", "escalation"
    string ToolName,
    bool Success,
    string? Summary,        // one-line summary of the result
    DateTimeOffset Timestamp
);