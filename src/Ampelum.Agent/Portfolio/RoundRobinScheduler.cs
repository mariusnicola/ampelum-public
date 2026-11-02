namespace SignalGrid.Ampelum.Agent;

/// <summary>Equal turns scheduling across workspaces. Stateful cursor advances on each call.</summary>
public sealed class RoundRobinScheduler : IWorkspaceScheduler
{
    private int _cursor;

    public WorkspaceId? SelectNext(
        IReadOnlyList<WorkspaceId> workspaceIds,
        IReadOnlyDictionary<WorkspaceId, WorkspaceContext> contexts)
    {
        if (workspaceIds.Count == 0) return null;

        var idx = Interlocked.Increment(ref _cursor) % workspaceIds.Count;
        return workspaceIds[Math.Abs(idx % workspaceIds.Count)];
    }
}