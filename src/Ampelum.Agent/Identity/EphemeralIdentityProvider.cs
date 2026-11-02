namespace SignalGrid.Ampelum.Agent;

/// <summary>Ephemeral in-memory identity provider for testing.
/// Issues unsigned tokens with instant expiry.
/// No signature verification — purely for isolated unit test scenarios.
/// Not suitable for any production use.</summary>
public sealed class EphemeralIdentityProvider : IAgentIdentityProvider
{
    private static readonly DateTimeOffset _instantExpiry = DateTimeOffset.UtcNow.AddSeconds(1);

    /// <summary>Issue a new ephemeral identity.</summary>
    public Task<AgentJwt> IssueAsync(
        string agentType,
        string[] scope,
        TimeSpan runDuration,
        CancellationToken ct = default)
    {
        var identity = new AgentIdentity(
            agentType,
            Guid.NewGuid().ToString("N"),
            scope,
            DateTimeOffset.UtcNow,
            _instantExpiry);

        // Ephemeral tokens are unsigned: format is agentType:instanceId
        var token = $"{identity.AgentType}:{identity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, identity));
    }

    /// <summary>Verify an ephemeral token. Always succeeds if format is valid.</summary>
    public Task<AgentIdentity?> VerifyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
            return Task.FromResult<AgentIdentity?>(null);

        var parts = token.Split(':');
        if (parts.Length != 2)
            return Task.FromResult<AgentIdentity?>(null);

        var identity = new AgentIdentity(
            parts[0],
            parts[1],
            [],
            DateTimeOffset.UtcNow,
            _instantExpiry);

        return Task.FromResult<AgentIdentity?>(identity);
    }

    /// <summary>Renew an ephemeral identity (returns new instant-expiry token).</summary>
    public Task<AgentJwt> RenewAsync(AgentJwt current, TimeSpan extension, CancellationToken ct = default)
    {
        var old = current.Claims;
        var newIdentity = new AgentIdentity(
            old.AgentType,
            old.InstanceId,
            old.Scope,
            DateTimeOffset.UtcNow,
            _instantExpiry,
            old.ResumedFrom);

        var token = $"{newIdentity.AgentType}:{newIdentity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, newIdentity));
    }

    /// <summary>Resume a run from a persisted checkpoint with a new instance ID.</summary>
    public Task<AgentJwt> ResumeAsync(
        string agentType,
        string[] scope,
        string priorInstanceId,
        TimeSpan runDuration,
        CancellationToken ct = default)
    {
        var identity = new AgentIdentity(
            agentType,
            Guid.NewGuid().ToString("N"),
            scope,
            DateTimeOffset.UtcNow,
            _instantExpiry,
            priorInstanceId);

        var token = $"{identity.AgentType}:{identity.InstanceId}";
        return Task.FromResult(new AgentJwt(token, identity));
    }
}
