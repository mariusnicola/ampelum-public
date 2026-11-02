namespace SignalGrid.Ampelum.Agent;

/// <summary>5 trust levels for progressive trust escalation.
/// None → Unknown → Provisional → Established → Trusted.</summary>
public enum TrustLevel
{
    /// <summary>Identity verification failed — all actions blocked.</summary>
    None = 0,

    /// <summary>No history — safe-tier tools only, RequireConfirmation for Mutating.</summary>
    Unknown = 1,

    /// <summary>Some history — Mutating tools allowed with session grant.</summary>
    Provisional = 2,

    /// <summary>Solid history — most tools auto-approved per tier defaults.</summary>
    Established = 3,

    /// <summary>Verified, long history — full PolicyEngine defaults apply.</summary>
    Trusted = 4,
}

/// <summary>Outcome of a run, used to update trust history.</summary>
public sealed record TrustOutcome(
    string RunInstanceId,           // AgentIdentity.InstanceId
    bool Succeeded,
    bool HadSecurityViolation,      // any governance block triggered
    bool HadEscalation,
    int ToolCallCount,
    DateTimeOffset OccurredAt
);

/// <summary>Track trust for an (AgentType, domain) pair.
/// Implements three-tier assignment: admin override → policy rules → default Unknown.</summary>
public interface IAgentTrustStore
{
    /// <summary>Get current trust level for an agent type in a domain.</summary>
    Task<TrustLevel> GetTrustLevelAsync(
        string agentType,           // AgentIdentity.AgentType (sub)
        string domain,              // e.g., "ampelum", "artisan.email", "external.acme-corp"
        CancellationToken ct = default);

    /// <summary>Record the outcome of a run for trust history.</summary>
    Task RecordOutcomeAsync(
        string agentType,
        string domain,
        TrustOutcome outcome,
        CancellationToken ct = default);

    /// <summary>Manually set trust level (admin override).</summary>
    Task SetTrustLevelAsync(
        string agentType,
        string domain,
        TrustLevel level,
        string reason,
        CancellationToken ct = default);

    /// <summary>Revoke trust (incident response — drops to None immediately).</summary>
    Task RevokeAsync(
        string agentType,
        string domain,
        string reason,
        CancellationToken ct = default);
}

/// <summary>Trust escalation policy rules (default — per spec).
/// Admin overrides are not subject to automatic escalation/demotion — they are stable until explicitly changed.</summary>
public sealed record TrustPolicy
{
    /// <summary>Consecutive successes to escalate (+1 level).</summary>
    public int SuccessThresholdForEscalation { get; init; } = 5;

    /// <summary>Security violations within the evaluation window to demote.</summary>
    public int ViolationsForDemotion { get; init; } = 1;

    /// <summary>Number of runs in the evaluation window for revocation.</summary>
    public int EvaluationWindowRuns { get; init; } = 10;

    /// <summary>Violations within evaluation window to fully revoke.</summary>
    public int ViolationsForRevocation { get; init; } = 3;

    /// <summary>Days of inactivity before trust decays to Unknown.</summary>
    public int InactivityDecayDays { get; init; } = 90;
}

/// <summary>Evaluates whether a given action is permitted for a caller at their current trust level.
/// Translates trust level + danger tier into PolicyDecision.
/// This is the bridge between IAgentTrustStore and PolicyEngine.</summary>
public interface IAgentTrustPolicy
{
    /// <summary>Evaluate tool permission based on caller's trust level.</summary>
    PolicyDecision EvaluateByTrust(
        TrustLevel callerTrust,
        ToolManifest tool,
        AgentIdentity caller);
}