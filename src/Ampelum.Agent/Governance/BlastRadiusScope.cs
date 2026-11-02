namespace SignalGrid.Ampelum.Agent;

/// <summary>Defines the maximum blast radius for an agent operation.
/// Per DAG node enforcement: paths, tools, domains, and max danger tier.</summary>
public sealed record BlastRadiusScope
{
    /// <summary>Allowed file-system paths (glob patterns). Empty = no file access. "*" = unrestricted.</summary>
    public IReadOnlySet<string> AllowedResourcePaths { get; init; } = new HashSet<string>();

    /// <summary>Allowed tool names. Empty = no tools. "*" = all non-catastrophic.</summary>
    public IReadOnlySet<string> AllowedToolNames { get; init; } = new HashSet<string>();

    /// <summary>Allowed network domains. Empty = no outbound. "*" = unrestricted.</summary>
    public IReadOnlySet<string> AllowedDomains { get; init; } = new HashSet<string>();

    /// <summary>Maximum danger tier allowed (inclusive). Defaults to Destructive.</summary>
    public DangerTier MaxAllowedTier { get; init; } = DangerTier.Destructive;

    /// <summary>Check if a tool is within this scope.</summary>
    public bool ContainsTool(string toolName) =>
        AllowedToolNames.Contains("*") || AllowedToolNames.Contains(toolName);

    /// <summary>Check if a path is within this scope.</summary>
    public bool ContainsPath(string path) =>
        AllowedResourcePaths.Contains("*") || AllowedResourcePaths.Any(p =>
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Escalation type for blocked operations.</summary>
public enum EscalationType
{
    /// <summary>Tool requires human confirmation.</summary>
    RequiresConfirmation,

    /// <summary>Tool is blocked by blast radius.</summary>
    BlastRadiusBreach,

    /// <summary>Tool is catastrophically blocked.</summary>
    CatastrophicBlock,

    /// <summary>Session budget exceeded.</summary>
    BudgetExceeded,

    /// <summary>Unknown skill requested.</summary>
    UnknownSkill,

    /// <summary>Trust level insufficient.</summary>
    TrustDenied,

    /// <summary>Custom escalation — metadata field carries details.</summary>
    Custom
}

/// <summary>Details of an escalation event — used by WorkflowStateMachine.</summary>
public sealed record EscalationContext(
    EscalationType Type,
    string ToolName,
    string Reason,
    DateTimeOffset OccurredAt,
    string? Metadata = null
);