namespace SignalGrid.Ampelum.Agent;

/// <summary>Generalized workflow state machine. Tracks phase progression,
/// enforces valid state transitions, and handles escalations.
/// No enums: TaskType and WorkflowPhase are strings.</summary>
public sealed class WorkflowStateMachine
{
    private readonly object _lock = new();
    private readonly HashSet<string> _validTransitions;
    private int _totalTasks;
    private int _completedTasks;
    private int _failedTasks;
    private DateTime _phaseStartedAt;

    public string WorkflowId { get; }
    public string CurrentPhase { get; private set; }
    public WorkflowState CurrentState { get; private set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? SuspendedAt { get; private set; }
    public EscalationContext? CurrentEscalation { get; private set; }
    public TimeSpan EscalationTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan PhaseTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool IsAutonomous { get; init; }

    public WorkflowStateMachine(string workflowId, string initialPhase, bool isAutonomous = false)
    {
        WorkflowId = workflowId;
        CurrentPhase = initialPhase;
        CurrentState = WorkflowState.Running;
        StartedAt = DateTimeOffset.UtcNow;
        _phaseStartedAt = DateTime.UtcNow;
        IsAutonomous = isAutonomous;
        _validTransitions = WorkflowStateTransitions.Allowed;
    }

    /// <summary>Advance to a new phase. Only allowed from Running state.</summary>
    public void AdvancePhase(string newPhase)
    {
        lock (_lock)
        {
            if (CurrentState != WorkflowState.Running)
                throw new InvalidOperationException(
                    $"Cannot advance phase in state {CurrentState}. Phase: {CurrentPhase} → {newPhase}");
            CurrentPhase = newPhase;
            _phaseStartedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Escalate to Suspended. Stores context for later resolution.</summary>
    public void Escalate(EscalationContext context)
    {
        lock (_lock)
        {
            if (CurrentState != WorkflowState.Running)
                throw new InvalidOperationException(
                    $"Cannot escalate from state {CurrentState}.");
            CurrentState = WorkflowState.Suspended;
            SuspendedAt = DateTimeOffset.UtcNow;
            CurrentEscalation = context;
        }
    }

    /// <summary>Resolve a Suspended escalation and return to Running.</summary>
    public void ResolveEscalation()
    {
        lock (_lock)
        {
            if (CurrentState != WorkflowState.Suspended)
                throw new InvalidOperationException(
                    $"Cannot resolve escalation from state {CurrentState}.");
            CurrentState = WorkflowState.Running;
            SuspendedAt = null;
            CurrentEscalation = null;
        }
    }

    /// <summary>Check if escalation has timed out. In autonomous mode, auto-abort.
    /// In interactive mode, keeps suspended awaiting human intervention.</summary>
    public bool IsEscalationTimedOut()
    {
        if (CurrentState != WorkflowState.Suspended || SuspendedAt == null)
            return false;
        return DateTimeOffset.UtcNow - SuspendedAt.Value >= EscalationTimeout;
    }

    /// <summary>Mark workflow as Completed (success).</summary>
    public void Complete()
    {
        lock (_lock)
        {
            if (CurrentState != WorkflowState.Running)
                throw new InvalidOperationException(
                    $"Cannot complete from state {CurrentState}.");
            CurrentState = WorkflowState.Completed;
        }
    }

    /// <summary>Mark workflow as Failed.</summary>
    public void Fail()
    {
        lock (_lock)
        {
            CurrentState = WorkflowState.Failed;
        }
    }

    /// <summary>Abort the workflow (autonomous timeout or explicit cancel).</summary>
    public void Abort()
    {
        lock (_lock)
        {
            CurrentState = WorkflowState.Aborted;
        }
    }

    /// <summary>Update task progress counters.</summary>
    public void UpdateProgress(int total, int completed, int failed)
    {
        lock (_lock)
        {
            _totalTasks = total;
            _completedTasks = completed;
            _failedTasks = failed;
        }
    }

    /// <summary>Get current progress as a tuple (total, completed, failed).</summary>
    public (int Total, int Completed, int Failed) GetProgress()
    {
        lock (_lock)
        {
            return (_totalTasks, _completedTasks, _failedTasks);
        }
    }

    /// <summary>Checkpoint the current workflow state. Stub implementation — wired to IStateStore in long-horizon spec.</summary>
    public Task CheckpointAsync(CancellationToken ct = default)
    {
        // Future: integrate with IStateStore.SaveAsync(WorkflowExecution)
        return Task.CompletedTask;
    }

    /// <summary>Check if the current phase has exceeded its timeout.</summary>
    public bool HasPhaseTimedOut()
    {
        lock (_lock)
        {
            var elapsed = DateTime.UtcNow - _phaseStartedAt;
            return elapsed > PhaseTimeout;
        }
    }

    /// <summary>Attempt to transition to a new state. Returns true if valid, false otherwise.</summary>
    public bool TryTransition(WorkflowState to)
    {
        lock (_lock)
        {
            var key = $"{CurrentState}→{to}";
            if (!_validTransitions.Contains(key)) return false;
            CurrentState = to;
            return true;
        }
    }
}

/// <summary>Workflow states.</summary>
public enum WorkflowState
{
    Running,
    Suspended,
    Completed,
    Failed,
    Aborted
}

/// <summary>All valid state transitions for the workflow state machine.</summary>
public static class WorkflowStateTransitions
{
    /// <summary>Set of all valid transition strings as "From→To".</summary>
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Running→Suspended",
        "Running→Completed",
        "Running→Failed",
        "Running→Aborted",
        "Suspended→Running",   // escalation resolved
        "Suspended→Aborted",   // escalation timed out (autonomous)
        "Suspended→Failed",    // escalation resolved with failure
    };
}