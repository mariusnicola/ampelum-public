namespace SignalGrid.Ampelum.Agent;

/// <summary>Decision returned by PolicyEngine for a tool invocation request.</summary>
public enum Decision
{
    /// <summary>Tool may execute immediately. No gate.</summary>
    Allow,

    /// <summary>Tool requires explicit confirmation (human or automated gate) before execution.</summary>
    RequireConfirmation,

    /// <summary>Tool is explicitly blocked. Caller should escalate.</summary>
    Block
}

/// <summary>Complete policy decision with optional denial reason for audit.</summary>
public sealed record PolicyDecision(Decision Decision, string? Reason = null)
{
    public static PolicyDecision Allowed => new(Decision.Allow);
    public static PolicyDecision Blocked(string reason) => new(Decision.Block, reason);
    public static PolicyDecision ConfirmationRequired(string reason) => new(Decision.RequireConfirmation, reason);
}