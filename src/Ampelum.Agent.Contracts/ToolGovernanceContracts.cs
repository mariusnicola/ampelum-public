using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agent.Contracts;

/// <summary>Risk tier declared by a tool manifest.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum ToolDangerTier
{
    /// <summary>Read-only or otherwise harmless operation.</summary>
    Safe,
    /// <summary>Operation that adds state without modifying existing state.</summary>
    Additive,
    /// <summary>Operation that changes existing state.</summary>
    Mutating,
    /// <summary>Operation that removes or irreversibly damages state.</summary>
    Destructive,
    /// <summary>Operation prohibited by default because its blast radius is unacceptable.</summary>
    Catastrophic,
}

/// <summary>Whether a tool action can be undone safely.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum ToolReversibility
{
    /// <summary>Operation can be directly reversed.</summary>
    Reversible,
    /// <summary>Operation can be offset through a separate compensating action.</summary>
    Compensatable,
    /// <summary>Operation cannot be reversed safely.</summary>
    Irreversible,
}

/// <summary>Runtime-neutral description of an available agent tool.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ToolManifestContract(
    string ToolId,
    string Name,
    string Description,
    ToolDangerTier DangerTier,
    ToolReversibility Reversibility,
    bool RequiresConfirmation,
    IReadOnlyDictionary<string, string>? InputSchema = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Disposition returned by a tool policy evaluation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public enum ToolPolicyDisposition
{
    /// <summary>Invocation may proceed without an additional gate.</summary>
    Allow,
    /// <summary>Invocation requires a recorded approval before it may proceed.</summary>
    RequireConfirmation,
    /// <summary>Invocation must not proceed.</summary>
    Block,
}

/// <summary>Policy outcome for one proposed tool invocation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ToolPolicyDecision(
    ToolPolicyDisposition Disposition,
    string Reason,
    string PolicyId,
    DateTimeOffset EvaluatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Human or external-gate confirmation required before executing an action.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ConfirmationRequest(
    string ConfirmationId,
    string ExecutionId,
    string ToolId,
    string Summary,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Terminal response to a confirmation request.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ConfirmationResponse(
    string ConfirmationId,
    bool Approved,
    string RespondedBy,
    DateTimeOffset RespondedAt,
    string? Reason = null);
