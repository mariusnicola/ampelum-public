namespace SignalGrid.Ampelum.Agent;

/// <summary>Lightweight context message for phase context building.
/// Role is "system" or "user".</summary>
public sealed record PhaseContextMessage(string Role, string Content);

/// <summary>Manages long-horizon context across phase boundaries.
/// Phase transitions archive completed-phase context and re-inject the GoalAnchor.
/// EpisodicMemory compression is the last-resort fallback for token budget exhaustion.</summary>
public sealed class MemoryManager
{
    private readonly IStateStore _stateStore;

    public MemoryManager(IStateStore stateStore)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>Archive the current phase and advance to the next.
    /// The GoalAnchor is preserved. ActiveContext from the completed phase is archived.
    /// Returns a new checkpoint with the updated CompletedPhases list.</summary>
    public async Task<PhaseCheckpoint> ArchivePhaseAsync(
        PhaseCheckpoint current,
        string nextPhase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(nextPhase);

        // Build summary for the completed phase
        var summary = new PhaseSummary
        {
            PhaseName = current.Phase,
            Succeeded = true,
            TasksCompleted = 0,
            TasksFailed = 0,
        };

        var updatedPhases = current.CompletedPhases.Append(summary).ToList();

        // Create new checkpoint: goal anchor preserved, active context cleared
        var next = current with
        {
            CheckpointId = current.CheckpointId ?? Guid.NewGuid().ToString("N"),
            PreviousCheckpointId = current.CheckpointId,
            Phase = nextPhase,
            PhaseIndex = current.PhaseIndex + 1,
            CompletedPhases = updatedPhases,
            ActiveContext = null, // cleared at phase boundary
            CapturedAt = DateTimeOffset.UtcNow,
        };

        await _stateStore.SaveAsync(next, ct).ConfigureAwait(false);
        return next;
    }

    /// <summary>Build the context to inject at the start of a phase.
    /// Always includes: GoalAnchor as system message + PhaseSummaries.
    /// Never suppresses GoalAnchor — it is unconditional.</summary>
    public IReadOnlyList<PhaseContextMessage> BuildPhaseContext(PhaseCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var messages = new List<PhaseContextMessage>();

        // GoalAnchor is ALWAYS first — unconditional
        messages.Add(new PhaseContextMessage("system",
            $"GOAL ANCHOR (never drop): {checkpoint.GoalAnchor}"));

        // Summaries of completed phases
        if (checkpoint.CompletedPhases.Count > 0)
        {
            var summaryText = string.Join("\n", checkpoint.CompletedPhases.Select(p =>
                $"Phase '{p.PhaseName}': {(p.Succeeded ? "completed" : "failed")}, " +
                $"{p.TasksCompleted} tasks completed, {p.TasksFailed} failed"));
            messages.Add(new PhaseContextMessage("system", $"Completed phases:\n{summaryText}"));
        }

        // Active context if any
        if (checkpoint.ActiveContext is { Length: > 0 } ctx)
            messages.Add(new PhaseContextMessage("system", $"Current context:\n{ctx}"));

        return messages;
    }
}
