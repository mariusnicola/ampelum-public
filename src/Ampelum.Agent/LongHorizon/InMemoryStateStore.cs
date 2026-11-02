namespace SignalGrid.Ampelum.Agent;

/// <summary>Thread-safe in-memory implementation of IStateStore for testing.</summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly Dictionary<string, List<PhaseCheckpoint>> _store = [];
    private readonly object _lock = new();

    public Task SaveAsync(PhaseCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        lock (_lock)
        {
            if (!_store.TryGetValue(checkpoint.WorkflowId, out var list))
                _store[checkpoint.WorkflowId] = list = [];
            list.Add(checkpoint);
        }
        return Task.CompletedTask;
    }

    public Task<PhaseCheckpoint?> LoadAsync(string workflowId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflowId);

        lock (_lock)
        {
            if (!_store.TryGetValue(workflowId, out var list) || list.Count == 0)
                return Task.FromResult<PhaseCheckpoint?>(null);
            return Task.FromResult<PhaseCheckpoint?>(list[^1]);
        }
    }

    public Task<PhaseCheckpoint?> LoadAsync(string workflowId, string? workspaceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflowId);

        lock (_lock)
        {
            if (!_store.TryGetValue(workflowId, out var list) || list.Count == 0)
                return Task.FromResult<PhaseCheckpoint?>(null);

            var filtered = workspaceId is null
                ? list
                : list.Where(c => c.WorkspaceId == workspaceId).ToList();

            if (filtered.Count == 0)
                return Task.FromResult<PhaseCheckpoint?>(null);

            return Task.FromResult<PhaseCheckpoint?>(filtered[^1]);
        }
    }

    public Task<PhaseCheckpoint?> LoadByIdAsync(string checkpointId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpointId);

        lock (_lock)
        {
            var checkpoint = _store.Values.SelectMany(x => x)
                .FirstOrDefault(c => c.CheckpointId == checkpointId);
            return Task.FromResult<PhaseCheckpoint?>(checkpoint);
        }
    }

    public Task<IReadOnlyList<PhaseCheckpoint>> ListAsync(string workflowId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflowId);

        lock (_lock)
        {
            if (!_store.TryGetValue(workflowId, out var list))
                return Task.FromResult<IReadOnlyList<PhaseCheckpoint>>([]);
            return Task.FromResult<IReadOnlyList<PhaseCheckpoint>>(list.AsReadOnly());
        }
    }

    public Task PurgeAsync(string workflowId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflowId);

        lock (_lock)
        {
            _store.Remove(workflowId);
        }
        return Task.CompletedTask;
    }
}
