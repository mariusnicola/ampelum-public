namespace SignalGrid.Ampelum.Agent;

/// <summary>Isolated execution context for a single project within a portfolio.</summary>
public sealed record WorkspaceContext : IAsyncDisposable
{
    public required WorkspaceId Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Empty string for non-filesystem workspaces (research, marketing).</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Maximum blast radius — per-task scopes may be narrower, never wider.</summary>
    public required BlastRadiusScope MaxBlastRadius { get; init; }

    /// <summary>Tool IDs available in this workspace.</summary>
    public IReadOnlySet<string> AllowedToolIds { get; init; } = new HashSet<string>();

    /// <summary>Scheduling priority 1–100. Higher = favored by PriorityWeightedScheduler.</summary>
    public int PriorityWeight { get; init; } = 50;

    /// <summary>Escalation policy for unattended/overnight runs.</summary>
    public EscalationPolicy EscalationPolicy { get; init; } = EscalationPolicy.AutoApprove();

    /// <summary>Session-isolated token store — one per workspace, never shared.</summary>
    public required ITokenStore TokenStore { get; init; }

    /// <summary>Secret store scoped to this workspace.</summary>
    public required ISecretStore SecretStore { get; init; }

    /// <summary>Skill registry snapshot for this workspace.</summary>
    public required ISkillRegistry Skills { get; init; }

    /// <summary>State partition key — defaults to WorkspaceId.Value.</summary>
    public string StatePartition => Id.Value;

    public async ValueTask DisposeAsync()
    {
        TokenStore.PurgeSession();
        await ValueTask.CompletedTask;
    }
}