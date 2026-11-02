using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum;

/// <summary>
/// An audit entry for a <see cref="VerifiedPredict{TSig}"/> attempt. Records the generator output,
/// critic verdict, and metadata for one prediction attempt within a retry loop.
/// Note: This is a standalone record, not derived from <see cref="SignalGrid.Ampelum.Audit.AuditEntry"/>
/// (which is sealed).
/// Properties: AuditId (unique ID), SignatureType (type name), AttemptNumber (1-based attempt),
/// GeneratorModel (generator's model ID or null), GeneratorOutput (the output Example),
/// CriticModel (critic's model ID or null), CriticVerdict (the verdict), Final (is final attempt),
/// Timestamp (UTC timestamp).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record VerifiedPredictAuditEntry(
    string AuditId,
    string SignatureType,
    int AttemptNumber,
    string? GeneratorModel,
    Example GeneratorOutput,
    string? CriticModel,
    CriticVerdict CriticVerdict,
    bool Final,
    DateTimeOffset Timestamp
);
