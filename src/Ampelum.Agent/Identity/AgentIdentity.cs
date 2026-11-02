namespace SignalGrid.Ampelum.Agent;

/// <summary>Issued at agent instantiation. Immutable for the lifetime of the run.</summary>
public sealed record AgentIdentity(
    string AgentType,           // stable — same across runs of the same agent class
    string InstanceId,          // unique per run — UUID
    string[] Scope,             // permitted tool tiers / resource namespaces
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? ResumedFrom = null  // prior instance ID for crash recovery linkage in audit trail
);

/// <summary>Signed, verifiable representation for external systems (A2A, Entra).</summary>
public sealed record AgentJwt(
    string Token,        // signed JWT string
    AgentIdentity Claims // parsed, already-verified claims
);

/// <summary>Issues and verifies agent identities.</summary>
public interface IAgentIdentityProvider
{
    /// <summary>Issue a new identity for a run. Called once at agent instantiation.</summary>
    Task<AgentJwt> IssueAsync(
        string agentType,
        string[] scope,
        TimeSpan runDuration,
        CancellationToken ct = default);

    /// <summary>Verify an inbound JWT (from A2A caller or checkpoint resume).</summary>
    Task<AgentIdentity?> VerifyAsync(string token, CancellationToken ct = default);

    /// <summary>Renew an expiring identity for a long-horizon run.</summary>
    Task<AgentJwt> RenewAsync(AgentJwt current, TimeSpan extension, CancellationToken ct = default);

    /// <summary>Resume a run from a persisted checkpoint.
    /// Issues a new instance ID, links the prior ID via ResumedFrom.
    /// Preserves agentType and scope, resets timestamps based on new run duration.</summary>
    Task<AgentJwt> ResumeAsync(
        string agentType,
        string[] scope,
        string priorInstanceId,
        TimeSpan runDuration,
        CancellationToken ct = default);
}