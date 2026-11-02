namespace SignalGrid.Ampelum.Agent;

/// <summary>Result of a single workspace execution within a portfolio run.</summary>
public sealed record WorkspaceExecutionResult
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required bool Success { get; init; }
    public IReadOnlyList<TaskExecutionResult> TaskResults { get; init; } = Array.Empty<TaskExecutionResult>();
    public string? Error { get; init; }

    /// <summary>Domain-agnostic success judgment. Null when no evaluator was configured.
    /// Populated by ISuccessEvaluator when provided to PortfolioOrchestrator.</summary>
    public SuccessJudgment? Judgment { get; init; }
}

/// <summary>Aggregate result of a portfolio run across all target workspaces.</summary>
public sealed record PortfolioResult
{
    public required bool OverallSuccess { get; init; }
    public IReadOnlyList<WorkspaceExecutionResult> CompletedWorkspaces { get; init; } = Array.Empty<WorkspaceExecutionResult>();
    public IReadOnlyList<(WorkspaceId Id, Exception Exception)> FailedWorkspaces { get; init; } = Array.Empty<(WorkspaceId, Exception)>();
    public IReadOnlyList<PendingEscalation> PendingEscalations { get; init; } = Array.Empty<PendingEscalation>();
    public string? Summary { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>An operation that was queued for human review during an unattended run.</summary>
public sealed record PendingEscalation
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string ToolName { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Progress event emitted during a portfolio run for streaming/realtime display.</summary>
public sealed record PortfolioProgressEvent
{
    public required WorkspaceId WorkspaceId { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public PortfolioProgressKind Kind { get; init; } = PortfolioProgressKind.Info;
}

public enum PortfolioProgressKind
{
    Info,
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    Escalation,
    WorkspaceCompleted,
    WorkspaceFailed,
}