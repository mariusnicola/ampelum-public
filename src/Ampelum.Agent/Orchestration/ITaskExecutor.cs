namespace SignalGrid.Ampelum.Agent;

/// <summary>Plugin architecture for task executors. Matched by string TaskType discriminator.
/// Each executor handles one type of DAG node (e.g., "implement", "test", "review").</summary>
public interface ITaskExecutor
{
    /// <summary>The TaskType string this executor handles.</summary>
    string TaskType { get; }

    /// <summary>Execute a single task node within a workflow.</summary>
    Task<TaskExecutionResult> ExecuteAsync(TaskNode node, CancellationToken ct = default);
}

/// <summary>Typed task executor — new interface matching spec's plugin API.
/// Use this when implementing executors for TaskDefinition-based workflows.
/// Do NOT use the legacy ITaskExecutor interface for new code.</summary>
public interface ITypedTaskExecutor
{
    /// <summary>The TaskType string this executor handles.</summary>
    string TaskType { get; }

    /// <summary>Execute a typed task definition.</summary>
    Task<TaskResult> ExecuteAsync(
        TaskDefinition task,
        AgentIdentity callerIdentity,
        CancellationToken ct = default);
}

/// <summary>Result of executing a typed task definition.</summary>
public sealed record TaskResult(
    string TaskId,
    bool Success,
    string? Output,
    string? Error,
    IReadOnlyDictionary<string, object?> Outputs,
    TimeSpan Duration
);

/// <summary>A single node in a workflow DAG.</summary>
public sealed record TaskNode
{
    /// <summary>Unique node identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Task type discriminator — used to find the right ITaskExecutor.</summary>
    public required string TaskType { get; init; }

    /// <summary>Human-readable description of the task.</summary>
    public required string Description { get; init; }

    /// <summary>Node IDs this node depends on. Must complete before this one starts.</summary>
    public IReadOnlySet<string> DependsOn { get; init; } = new HashSet<string>();

    /// <summary>Arbitrary extension metadata.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

    /// <summary>Blast radius scope for this node.</summary>
    public BlastRadiusScope? BlastRadius { get; init; }
}

/// <summary>Result of executing a single task node.</summary>
public sealed record TaskExecutionResult
{
    /// <summary>Whether the task completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Output or error message. Null if no output.</summary>
    public string? Output { get; init; }

    /// <summary>Error details if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Tokens consumed by this task.</summary>
    public long TokensUsed { get; init; }

    /// <summary>Execution duration.</summary>
    public TimeSpan Duration { get; init; }
}