namespace SignalGrid.Ampelum.Agent;

/// <summary>
/// Represents the persistent execution state of a workflow.
/// Returned by WorkflowStateMachine.Current and used for checkpointing.
/// </summary>
public sealed record WorkflowExecution(
    string WorkflowId,
    string WorkflowType,
    string AgentType,
    string AgentInstanceId,
    WorkflowState CurrentState,
    string CurrentPhase,
    DateTime StartedAt,
    DateTime PhaseStartedAt,
    TimeSpan PhaseTimeout,
    int TotalTasks,
    int CompletedTasks,
    int FailedTasks,
    string? CheckpointId = null,
    EscalationContext? Escalation = null,
    IReadOnlyDictionary<string, object?>? Metadata = null
);
