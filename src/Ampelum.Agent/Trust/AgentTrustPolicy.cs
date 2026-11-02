namespace SignalGrid.Ampelum.Agent;

/// <summary>Default trust-based policy evaluation.
/// Translates trust level + tool danger tier into PolicyDecision.
/// Maps trust levels to tool tier defaults per spec section 2.</summary>
public sealed class AgentTrustPolicy : IAgentTrustPolicy
{
    /// <summary>Evaluate tool permission based on caller's trust level.
    /// Trust-to-policy mapping (per spec section 2):
    ///
    /// | Trust       | Mutating tools | Destructive tools       | Catastrophic |
    /// |-------------|----------------|------------------------|--------------|
    /// | None        | Block          | Block                  | Block        |
    /// | Unknown     | RequireConfirm | Block                  | Block        |
    /// | Provisional | SessionApprove | RequireConfirm         | Block        |
    /// | Established | AutoApprove    | RequireConfirm         | Block        |
    /// | Trusted     | AutoApprove    | SessionApprove         | Block        |
    ///
    /// Catastrophic is always blocked regardless of trust — hard invariant.</summary>
    public PolicyDecision EvaluateByTrust(
        TrustLevel callerTrust,
        ToolManifest tool,
        AgentIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(caller);

        // Hard invariant: Catastrophic tier always blocked (no override)
        if (tool.DangerTier == DangerTier.Catastrophic)
        {
            return PolicyDecision.Blocked(
                $"Trust level '{callerTrust}' cannot override Catastrophic tier block on tool '{tool.Name}'.");
        }

        // Trust.None: all actions blocked except Catastrophic (already handled)
        if (callerTrust == TrustLevel.None)
        {
            return PolicyDecision.Blocked(
                $"Agent '{caller.AgentType}' at trust level None is blocked from all tools.");
        }

        // Trust.Unknown: safe-tier only, RequireConfirmation for Mutating (Destructive+)
        if (callerTrust == TrustLevel.Unknown)
        {
            if (tool.DangerTier >= DangerTier.Destructive)
            {
                return PolicyDecision.ConfirmationRequired(
                    $"Agent '{caller.AgentType}' at trust level Unknown requires confirmation for '{tool.Name}' (tier: {tool.DangerTier}).");
            }

            return PolicyDecision.Allowed;
        }

        // Trust.Provisional: Mutating allowed via SessionGrant, Destructive requires confirmation
        if (callerTrust == TrustLevel.Provisional)
        {
            if (tool.DangerTier == DangerTier.Destructive)
            {
                return PolicyDecision.ConfirmationRequired(
                    $"Agent '{caller.AgentType}' at trust level Provisional requires confirmation for destructive tool '{tool.Name}'.");
            }

            // Mutating (Safe/Sensitive/External) allowed at Provisional
            return PolicyDecision.Allowed;
        }

        // Trust.Established: Mutating auto-approved, Destructive requires confirmation
        if (callerTrust == TrustLevel.Established)
        {
            if (tool.DangerTier == DangerTier.Destructive)
            {
                return PolicyDecision.ConfirmationRequired(
                    $"Agent '{caller.AgentType}' at trust level Established requires confirmation for destructive tool '{tool.Name}'.");
            }

            return PolicyDecision.Allowed;
        }

        // Trust.Trusted: most tools auto-approved, Destructive via SessionGrant
        if (callerTrust == TrustLevel.Trusted)
        {
            // All non-Catastrophic tools allowed at Trusted
            return PolicyDecision.Allowed;
        }

        // Fallback (should not reach)
        return PolicyDecision.Blocked($"Unknown trust level: {callerTrust}");
    }
}
