namespace SignalGrid.Ampelum.Agent;

/// <summary>Deterministic model router. Given a task signal, returns the appropriate IChatClient.
/// Three tiers: Fast (Haiku equivalent), Balanced (Sonnet), Capable (Opus).
/// No model names are hardcoded — routing table is configuration-driven.</summary>
public interface IModelRouter
{
    /// <summary>Route a task to the appropriate model tier.
    /// First matching rule wins (TaskType exact match before IsInteractive flag).</summary>
    ModelRoute Route(TaskSignal signal);

    /// <summary>Route with fallback chain: primary → tier fallback → lower tier → throw.</summary>
    ModelRoute RouteWithFallback(TaskSignal signal, Func<ModelTier, bool> isAvailable);
}

/// <summary>Signal describing the task to route. Used for model selection.</summary>
public sealed record TaskSignal
{
    /// <summary>Task type string discriminator (e.g., "implement", "spec_write", "search").</summary>
    public required string TaskType { get; init; }

    /// <summary>True if this is an interactive (user-facing) task — needs lower latency.</summary>
    public bool IsInteractive { get; init; }

    /// <summary>Override tier — if set, bypasses routing table.</summary>
    public ModelTier? RequiredTier { get; init; }

    /// <summary>True if this task requires complex planning or reasoning — prefers higher capability tier.</summary>
    public bool RequiresReasoning { get; init; }

    /// <summary>Estimated token count for the task — budget signal for large context tasks.</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>Model capability tiers.</summary>
public enum ModelTier
{
    /// <summary>Least capable, lowest latency. For simple completions and tool calls.</summary>
    Fast = 0,

    /// <summary>Balanced capability and latency. Default for most tasks.</summary>
    Balanced = 1,

    /// <summary>Most capable, highest latency. For complex reasoning and spec writing.</summary>
    Capable = 2,
}

/// <summary>Result of routing a task signal to a model tier.</summary>
public sealed record ModelRoute(ModelTier Tier, string ModelId, string Provider);