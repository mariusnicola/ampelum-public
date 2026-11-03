using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>
/// Durable memory of the run: the goal, which phases are done, kept in a state store that survives a restart.
/// After a restart the notes are turned back into prompt context, so the goal is never dropped.
/// </summary>
public sealed class PhaseNotes(IStateStore store)
{
    readonly MemoryManager _memory = new(store);

    /// <summary>Writes the first checkpoint for a run. Does nothing when notes already exist, as after a restart.</summary>
    public async Task OpenAsync(string runId, string goal, string firstPhase, CancellationToken ct = default)
    {
        if (await store.LoadAsync(runId, ct).ConfigureAwait(false) is not null)
            return;
        await store.SaveAsync(new PhaseCheckpoint
        {
            CheckpointId = Guid.NewGuid().ToString("N"),
            WorkflowId = runId,
            Phase = firstPhase,
            GoalAnchor = goal,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Closes the current phase and moves the notes to the next one.</summary>
    public async Task<PhaseCheckpoint> ArchiveAsync(string runId, string nextPhase, CancellationToken ct = default)
    {
        var current = await store.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No notes for run '{runId}'.");
        return await _memory.ArchivePhaseAsync(current, nextPhase, ct).ConfigureAwait(false);
    }

    /// <summary>The notes as context messages: the goal first, then one line per finished phase.</summary>
    public async Task<IReadOnlyList<PhaseContextMessage>> ContextAsync(string runId, CancellationToken ct = default)
    {
        var current = await store.LoadAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No notes for run '{runId}'.");
        return _memory.BuildPhaseContext(current);
    }
}
