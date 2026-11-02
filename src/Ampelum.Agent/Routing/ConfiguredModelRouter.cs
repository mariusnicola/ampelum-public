using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Deterministic model router driven by a routing table. Applies rule priority: explicit tier override, TaskType rule, RequiresReasoning, IsInteractive, fallback tier.</summary>
public sealed class ConfiguredModelRouter : IModelRouter
{
    private readonly ModelRoutingTable _table;
    private readonly ILogger _logger;

    /// <summary>Create a new router with the given routing table and optional logger.</summary>
    public ConfiguredModelRouter(ModelRoutingTable table, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Route a task signal to the appropriate model tier based on the routing table.</summary>
    public ModelRoute Route(TaskSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var tier = ResolveTier(signal);
        return ResolveRoute(tier, _ => true);
    }

    /// <summary>Route with fallback: if the preferred tier is unavailable, step down to lower tiers.</summary>
    public ModelRoute RouteWithFallback(TaskSignal signal, Func<ModelTier, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(isAvailable);
        var tier = ResolveTier(signal);
        return ResolveRoute(tier, isAvailable);
    }

    private ModelTier ResolveTier(TaskSignal signal)
    {
        // 1. Explicit RequiredTier override takes highest priority
        if (signal.RequiredTier.HasValue)
        {
            _logger.LogDebug("Routing task '{TaskType}' to explicit tier {Tier}", signal.TaskType, signal.RequiredTier.Value);
            return signal.RequiredTier.Value;
        }

        // 2. TaskType exact match
        var rule = _table.Rules.FirstOrDefault(r =>
            string.Equals(r.TaskType, signal.TaskType, StringComparison.OrdinalIgnoreCase));
        if (rule != null)
        {
            _logger.LogDebug("Routing task '{TaskType}' to tier {Tier} via rule: {Reason}",
                signal.TaskType, rule.Tier, rule.Reason);
            return rule.Tier;
        }

        // 3. RequiresReasoning → prefer Capable tier
        if (signal.RequiresReasoning)
        {
            _logger.LogDebug("Task '{TaskType}' requires reasoning, routing to Capable", signal.TaskType);
            return ModelTier.Capable;
        }

        // 4. IsInteractive → prefer Fast tier for lower latency
        if (signal.IsInteractive)
        {
            _logger.LogDebug("Task '{TaskType}' is interactive, routing to Fast", signal.TaskType);
            return ModelTier.Fast;
        }

        // 5. Fallback to default tier from table
        _logger.LogDebug("Task '{TaskType}' matched no rules, using fallback tier {Tier}",
            signal.TaskType, _table.FallbackTier);
        return _table.FallbackTier;
    }

    private ModelRoute ResolveRoute(ModelTier preferredTier, Func<ModelTier, bool> isAvailable)
    {
        // Try tiers in order: preferred, then step down to lower tiers
        var tiersToTry = GetTierFallbackChain(preferredTier);

        foreach (var tier in tiersToTry)
        {
            if (!_table.Tiers.TryGetValue(tier, out var config))
            {
                continue;
            }

            // Check if this tier is available
            if (isAvailable(tier))
            {
                _logger.LogDebug("Resolved route: tier={Tier}, modelId={ModelId}, provider={Provider}",
                    tier, config.PrimaryModelId, config.Provider);
                return new ModelRoute(tier, config.PrimaryModelId, config.Provider);
            }
        }

        // All tiers exhausted
        throw new InvalidOperationException(
            "All model tiers are unavailable. Cannot route task. Check model availability and routing table configuration.");
    }

    private static IEnumerable<ModelTier> GetTierFallbackChain(ModelTier preferred)
    {
        // Yield tiers in order: preferred, then step DOWN to lower capability (but ONLY if preferred is higher)
        // Fast (0) < Balanced (1) < Capable (2)
        // So from Capable, we try: Capable, Balanced, Fast
        // From Balanced, we try: Balanced, Fast
        // From Fast, we try: Fast only
        yield return preferred;

        if (preferred == ModelTier.Capable)
        {
            yield return ModelTier.Balanced;
            yield return ModelTier.Fast;
        }
        else if (preferred == ModelTier.Balanced)
        {
            yield return ModelTier.Fast;
        }
        // From Fast, no fallback (it's the lowest tier)
    }
}
