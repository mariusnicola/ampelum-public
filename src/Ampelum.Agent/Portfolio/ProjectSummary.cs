namespace SignalGrid.Ampelum.Agent;

/// <summary>Lightweight summary of a project's current state, gathered in a read-only pass
/// before any LLM calls. Used by IPortfolioIntentDecomposer to generate task DAGs.</summary>
public sealed record ProjectSummary
{
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Current phase from roadmap file, if found.</summary>
    public string CurrentPhase { get; init; } = string.Empty;

    /// <summary>Top backlog items from backlog file, if found.</summary>
    public IReadOnlyList<string> TopBacklogItems { get; init; } = Array.Empty<string>();

    /// <summary>Outcome of the last execution, if any.</summary>
    public string? LastExecutionOutcome { get; init; }

    /// <summary>Current open blockers.</summary>
    public IReadOnlyList<string> OpenBlockers { get; init; } = Array.Empty<string>();

    public DateTimeOffset RefreshedAt { get; init; }
}