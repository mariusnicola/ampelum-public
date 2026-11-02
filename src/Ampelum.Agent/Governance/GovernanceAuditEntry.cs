namespace SignalGrid.Ampelum.Agent;

/// <summary>Immutable audit record for a governance decision. Every PolicyEngine evaluation
/// is logged regardless of outcome (Allow, RequireConfirmation, Block).
/// The audit log is append-only; it does not implement a cryptographic hash chain
/// between entries.
/// NOT CURRENTLY WIRED: no runtime path (<see cref="ToolDispatcher"/>, <c>PolicyEngine</c>,
/// <c>SqliteToolAuditLog</c>) constructs or consumes this type today — it exists but is dead
/// code outside of its own round-trip test.</summary>
public sealed record GovernanceAuditEntry(
    string AuditId,
    string ActorId,
    string ActorType,
    string ToolName,
    PolicyDecision Decision,
    string DecisionReason,
    IReadOnlyDictionary<string, object?> Args,
    DateTimeOffset Timestamp,
    string? BlastRadiusScopeId = null
);
