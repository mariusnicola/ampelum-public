namespace SignalGrid.Ampelum.Agent;

/// <summary>Thrown when attempting to register a WorkspaceId that already exists in the portfolio.</summary>
public sealed class DuplicateWorkspaceException : InvalidOperationException
{
    public DuplicateWorkspaceException(WorkspaceId id)
        : base($"Workspace '{id}' is already registered.") { }
}