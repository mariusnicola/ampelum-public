namespace SignalGrid.Ampelum.Agent;

/// <summary>Selects the next workspace to draw a task from. Used by PortfolioOrchestrator
/// to control cross-workspace scheduling order.</summary>
public interface IWorkspaceScheduler
{
    /// <summary>Select the next workspace with at least one ready task.
    /// Returns null if workspaceIds is empty.</summary>
    WorkspaceId? SelectNext(
        IReadOnlyList<WorkspaceId> workspaceIds,
        IReadOnlyDictionary<WorkspaceId, WorkspaceContext> contexts);
}