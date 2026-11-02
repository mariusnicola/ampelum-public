namespace SignalGrid.Ampelum.Agent;

/// <summary>A single task node within a portfolio workspace's task DAG.
/// Produced by IPortfolioIntentDecomposer, consumed by PortfolioOrchestrator.</summary>
public sealed record TaskDefinition
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required string TaskType { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Domain-specific success criteria for this task.
    /// Interpreted by the workspace's ISuccessEvaluator.
    /// Null = use default evaluator (all tasks completed without error).</summary>
    public string? SuccessCriteria { get; init; }
}
