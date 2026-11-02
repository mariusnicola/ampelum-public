namespace SignalGrid.Ampelum.Agent;

/// <summary>Persistent registry of long-lived project workspaces within a portfolio.</summary>
public interface IProjectPortfolio
{
    /// <summary>Register a workspace. Throws DuplicateWorkspaceException if Id already registered.</summary>
    void Register(WorkspaceContext workspace);

    /// <summary>Deregister and dispose. No-op if not found.</summary>
    ValueTask DeregisterAsync(WorkspaceId id, CancellationToken ct = default);

    /// <summary>Returns null if not registered.</summary>
    WorkspaceContext? Get(WorkspaceId id);

    IReadOnlyList<WorkspaceContext> GetAll();

    bool IsEmpty { get; }
}