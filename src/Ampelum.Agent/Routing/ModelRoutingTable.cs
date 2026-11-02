namespace SignalGrid.Ampelum.Agent;

/// <summary>A routing rule that maps a task type to a preferred model tier.</summary>
public sealed record RoutingRule
{
    /// <summary>Task type discriminator (e.g., "implement", "spec_write").</summary>
    public required string TaskType { get; init; }

    /// <summary>Preferred model tier for this task type.</summary>
    public required ModelTier Tier { get; init; }

    /// <summary>True if this rule applies only to interactive tasks.</summary>
    public bool IsInteractive { get; init; }

    /// <summary>Reason for this routing decision (e.g., "Implementation follows a plan").</summary>
    public string? Reason { get; init; }
}

/// <summary>Configuration for a model tier: primary model, fallback, and provider.</summary>
public sealed record TierConfig
{
    /// <summary>Primary model ID for this tier.</summary>
    public required string PrimaryModelId { get; init; }

    /// <summary>Fallback model ID if primary is unavailable.</summary>
    public string? FallbackModelId { get; init; }

    /// <summary>Provider name.</summary>
    public string Provider { get; init; } = "default";
}

/// <summary>Deterministic routing table. Maps task types to model tiers with fallback chains.</summary>
public sealed class ModelRoutingTable
{
    /// <summary>Ordered list of routing rules. First match wins.</summary>
    public required IReadOnlyList<RoutingRule> Rules { get; init; }

    /// <summary>Default tier for tasks that do not match any rule.</summary>
    public ModelTier FallbackTier { get; init; } = ModelTier.Balanced;

    /// <summary>Tier configurations: tier -> (primary model, fallback model, provider).</summary>
    public required IReadOnlyDictionary<ModelTier, TierConfig> Tiers { get; init; }

    /// <summary>Standard task-type rules over caller-supplied tier configuration.</summary>
    public static ModelRoutingTable WithDefaultRules(IReadOnlyDictionary<ModelTier, TierConfig> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        return new ModelRoutingTable
        {
            Rules =
            [
                new RoutingRule { TaskType = "implement", Tier = ModelTier.Fast, Reason = "Implementation follows a plan" },
                new RoutingRule { TaskType = "spec_write", Tier = ModelTier.Capable, Reason = "Spec writing requires deep reasoning" },
                new RoutingRule { TaskType = "synthesize_skill", Tier = ModelTier.Capable },
                new RoutingRule { TaskType = "synthesize_workflow", Tier = ModelTier.Capable },
                new RoutingRule { TaskType = "review", Tier = ModelTier.Balanced },
                new RoutingRule { TaskType = "escalation_response", Tier = ModelTier.Balanced, IsInteractive = true },
            ],
            FallbackTier = ModelTier.Balanced,
            Tiers = tiers,
        };
    }
}
