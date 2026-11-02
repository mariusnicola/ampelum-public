namespace SignalGrid.Ampelum.Agent;

/// <summary>Options for a single portfolio run.</summary>
public sealed record PortfolioOptions
{
    /// <summary>Maximum projects executing simultaneously. 1 = sequential, null = all at once.</summary>
    public int MaxConcurrentProjects { get; init; } = 3;

    /// <summary>If set, run only on these specific workspaces (by WorkspaceId).</summary>
    public IReadOnlyList<WorkspaceId>? ProjectFilter { get; init; }

    /// <summary>If true, generate task DAG but block all writes. Reports what would happen.</summary>
    public bool DryRun { get; init; }

    /// <summary>Override per-workspace escalation policy for this run.</summary>
    public EscalationPolicy? EscalationOverride { get; init; }

    public static PortfolioOptions Default => new();

    /// <summary>Convenience preset for overnight unattended runs.</summary>
    public static PortfolioOptions Overnight => new()
    {
        MaxConcurrentProjects = 3,
        EscalationOverride = EscalationPolicy.AutoApprove(),
    };
}