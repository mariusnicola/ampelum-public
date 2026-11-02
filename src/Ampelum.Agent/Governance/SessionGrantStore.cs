namespace SignalGrid.Ampelum.Agent;

/// <summary>In-memory store for active session grants. Supports lookup by tool name
/// and caller instance, expiry tracking, and cleanup of expired grants.</summary>
public sealed class SessionGrantStore
{
    private readonly List<SessionGrant> _grants = [];

    /// <summary>Add a grant to the store.</summary>
    public void Add(SessionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _grants.Add(grant);
    }

    /// <summary>Find an active (not yet expired) grant for a specific tool and caller.
    /// Returns null if no match found or if matching grant is expired.</summary>
    public SessionGrant? FindActive(string toolName, string callerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(callerInstanceId);

        return _grants.FirstOrDefault(g =>
            g.ToolName == toolName &&
            g.CallerInstanceId == callerInstanceId &&
            !g.IsExpired);
    }

    /// <summary>Remove all expired grants from the store.</summary>
    public void RemoveExpired()
    {
        _grants.RemoveAll(g => g.IsExpired);
    }
}
