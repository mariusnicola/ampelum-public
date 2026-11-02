using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>Metadata emitted when a successful observation authorizes a later mutation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ObservationTokenIssuance(
    string TokenId,
    string ResourceKey,
    string IssuedByToolId,
    string ScopeId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Append-only audit event shared by workflow, policy, and evaluation boundaries.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AgentAuditEvent(
    string EventId,
    string EventType,
    string ExecutionId,
    DateTimeOffset OccurredAt,
    string ActorId,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
