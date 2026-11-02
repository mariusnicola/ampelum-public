namespace SignalGrid.Ampelum.Agent;

/// <summary>How a workspace handles tool invocations that exceed its auto-approval envelope.</summary>
public enum EscalationMode
{
    /// <summary>Auto-approve up to the configured MaxAutoApproveTier.</summary>
    AutoApprove,
    /// <summary>Queue blocked operations for human review.</summary>
    Queue,
    /// <summary>Skip blocked operations silently, continue the task.</summary>
    Skip,
    /// <summary>Abort the current task (other portfolio tasks continue).</summary>
    AbortTask,
}

/// <summary>Per-workspace policy for handling tool invocations outside the auto-approve envelope.
/// DangerTier.Catastrophic is ALWAYS blocked regardless of this policy — enforced by ToolDispatcher.</summary>
public sealed record EscalationPolicy
{
    public EscalationMode Mode { get; init; }

    /// <summary>Max DangerTier that can be auto-approved. Only meaningful in AutoApprove mode.
    /// DangerTier.Catastrophic is always blocked regardless of this setting.</summary>
    public DangerTier MaxAutoApproveTier { get; init; } = DangerTier.Destructive;

    /// <summary>Tool IDs that are never auto-approved even within the tier envelope.</summary>
    public IReadOnlySet<string> BlockList { get; init; } = new HashSet<string>();

    /// <summary>Auto-approve within workspace root, up to the specified tier.</summary>
    public static EscalationPolicy AutoApprove(
        DangerTier maxTier = DangerTier.Destructive,
        IReadOnlySet<string>? blockList = null) => new()
        {
            Mode = EscalationMode.AutoApprove,
            MaxAutoApproveTier = maxTier,
            BlockList = blockList ?? new HashSet<string>()
        };

    /// <summary>Queue blocked operations for morning review.</summary>
    public static EscalationPolicy Queue() => new() { Mode = EscalationMode.Queue };

    /// <summary>Skip blocked operations silently.</summary>
    public static EscalationPolicy Skip() => new() { Mode = EscalationMode.Skip };

    /// <summary>Abort the current task, continue other portfolio tasks.</summary>
    public static EscalationPolicy AbortTask() => new() { Mode = EscalationMode.AbortTask };
}