namespace SignalGrid.Ampelum.Agent;

/// <summary>Configuration options for SubagentOrchestrator execution.</summary>
public sealed class OrchestratorOptions
{
    /// <summary>Maximum number of tasks to execute in parallel. Default: 4.</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Default timeout per task if not specified in TaskDefinition. Default: 30 minutes.</summary>
    public TimeSpan DefaultTaskTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Result of executing a typed workflow DAG.</summary>
public sealed record OrchestratorResult(
    bool Success,
    IReadOnlyList<TaskResult> Results,
    IReadOnlyList<string> FailedTaskIds,
    IReadOnlyList<string> SkippedTaskIds,
    TimeSpan TotalDuration
);
