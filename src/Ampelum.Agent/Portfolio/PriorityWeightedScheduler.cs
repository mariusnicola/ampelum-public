namespace SignalGrid.Ampelum.Agent;

/// <summary>Weighted random selection by PriorityWeight. Higher weight = selected more often.
/// Accepts optional seed for deterministic test runs.</summary>
public sealed class PriorityWeightedScheduler : IWorkspaceScheduler
{
    private readonly Random _rng;

    public PriorityWeightedScheduler(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public WorkspaceId? SelectNext(
        IReadOnlyList<WorkspaceId> workspaceIds,
        IReadOnlyDictionary<WorkspaceId, WorkspaceContext> contexts)
    {
        if (workspaceIds.Count == 0) return null;

        var totalWeight = workspaceIds.Sum(id =>
            (long)Math.Max(1, contexts.GetValueOrDefault(id)?.PriorityWeight ?? 1));

        var roll = _rng.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var id in workspaceIds)
        {
            cumulative += Math.Max(1, contexts.GetValueOrDefault(id)?.PriorityWeight ?? 1);
            if (roll <= cumulative) return id;
        }

        return workspaceIds[^1]; // fallback
    }
}