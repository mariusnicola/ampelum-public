namespace SignalGrid.Ampelum.Agent;

/// <summary>Deterministic policy engine. Evaluates tool invocation requests based on
/// danger tier, reversibility, custom policies, and session grants. Never uses LLM.
/// Evaluation order: Catastrophic hard block â†’ custom policies â†’ confirmation rules â†’ session grants â†’ allow by default.</summary>
public sealed class PolicyEngine
{
    private readonly IReadOnlyList<IToolPolicy> _customPolicies;
    private readonly SessionGrantStore? _grantStore;

    /// <summary>Default constructor with no custom policies or grant store.</summary>
    public PolicyEngine() : this([], null)
    {
    }

    /// <summary>Constructor with custom policies and optional grant store.</summary>
    /// <param name="customPolicies">List of custom policy implementations to evaluate before tier defaults.</param>
    /// <param name="grantStore">Optional session grant store for permission promotions.</param>
    public PolicyEngine(IReadOnlyList<IToolPolicy> customPolicies, SessionGrantStore? grantStore = null)
    {
        ArgumentNullException.ThrowIfNull(customPolicies);
        _customPolicies = customPolicies;
        _grantStore = grantStore;
    }

    /// <summary>Evaluate whether a tool invocation is allowed.
    /// Catastrophic tier tools are always Blocked regardless of trust level.
    /// Evaluation order (first match wins): Catastrophic block â†’ custom policies â†’ confirmation rules â†’ session grants â†’ allow.</summary>
    public PolicyDecision Evaluate(ToolManifest tool, PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Catastrophic tier: hard block (no override possible)
        if (tool.DangerTier == DangerTier.Catastrophic)
        {
            return PolicyDecision.Blocked("Catastrophic-tier tools are permanently blocked.");
        }

        // 2. Custom policies: evaluate in order. Block rules are checked first.
        var args = context.Args ?? new Dictionary<string, object?>();
        foreach (var policy in _customPolicies)
        {
            var customDecision = policy.Evaluate(tool, context.Caller, args);
            if (customDecision.Decision == Decision.Block)
            {
                return customDecision;
            }
        }

        // 3. Session grant check: active grants promote before tier defaults
        if (_grantStore != null)
        {
            var grant = _grantStore.FindActive(tool.Name, context.Caller.InstanceId);
            if (grant != null && grant.GrantedDecision.Decision == Decision.Allow)
                return PolicyDecision.Allowed;
        }
        // Also check context SessionGrant for backward compatibility
        if (context.SessionGrant != null &&
            context.SessionGrant.ToolName == tool.Name &&
            !context.SessionGrant.IsExpired &&
            context.SessionGrant.GrantedDecision.Decision == Decision.Allow)
        {
            return PolicyDecision.Allowed;
        }

        // 4. RequiresConfirmation flag
        if (tool.RequiresConfirmation)
        {
            return PolicyDecision.ConfirmationRequired(
                $"Tool '{tool.Name}' requires explicit confirmation.");
        }

        // 5. Destructive irreversible: require confirmation
        if (tool.DangerTier == DangerTier.Destructive && tool.Reversibility == Reversibility.Irreversible)
        {
            return PolicyDecision.ConfirmationRequired(
                $"Destructive + Irreversible tool '{tool.Name}' requires confirmation.");
        }

        // 6. Default: allow
        return PolicyDecision.Allowed;
    }
}

/// <summary>Context for a policy evaluation â€” includes caller identity, session state, and tool args.</summary>
public sealed record PolicyContext
{
    /// <summary>Caller agent identity â€” used for audit trail.</summary>
    public required AgentIdentity Caller { get; init; }

    /// <summary>Optional session grant that can elevate tool permissions.</summary>
    public SessionGrant? SessionGrant { get; init; }

    /// <summary>Tool invocation arguments (aliases only, never raw secret values).</summary>
    public IReadOnlyDictionary<string, object?>? Args { get; init; }
}
