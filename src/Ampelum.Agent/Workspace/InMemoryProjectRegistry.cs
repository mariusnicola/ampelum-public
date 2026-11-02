using System.Collections.Concurrent;

namespace SignalGrid.Ampelum.Agent;

/// <summary>In-memory portfolio registry. Thread-safe, no persistence.</summary>
public sealed class InMemoryProjectRegistry : IProjectPortfolio
{
    private readonly ConcurrentDictionary<WorkspaceId, WorkspaceContext> _workspaces = new();

    public void Register(WorkspaceContext workspace)
    {
        if (!_workspaces.TryAdd(workspace.Id, workspace))
            throw new DuplicateWorkspaceException(workspace.Id);
    }

    public async ValueTask DeregisterAsync(WorkspaceId id, CancellationToken ct = default)
    {
        if (_workspaces.TryRemove(id, out var ws))
        {
            await ws.DisposeAsync();
        }
    }

    public WorkspaceContext? Get(WorkspaceId id) =>
        _workspaces.TryGetValue(id, out var ws) ? ws : null;

    public IReadOnlyList<WorkspaceContext> GetAll() =>
        _workspaces.Values.ToList();

    public bool IsEmpty => _workspaces.IsEmpty;
}