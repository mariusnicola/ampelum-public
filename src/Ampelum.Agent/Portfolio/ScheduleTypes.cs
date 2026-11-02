namespace SignalGrid.Ampelum.Agent;

/// <summary>How often a schedule repeats.</summary>
public enum RepeatInterval
{
    /// <summary>Run once at the specified time, then removed.</summary>
    Once,
    /// <summary>Run at the specified time every day.</summary>
    Daily,
}

/// <summary>Cron-like expression for scheduling portfolio runs.
/// No actual cron syntax — just time-of-day and repeat.</summary>
public sealed record ScheduleExpression
{
    /// <summary>UTC time of day to run (e.g. 02:00 for 2 AM UTC).</summary>
    public TimeOnly TimeOfDay { get; init; }

    /// <summary>Repeat behavior. Defaults to Daily.</summary>
    public RepeatInterval Repeat { get; init; } = RepeatInterval.Daily;
}

/// <summary>A registered schedule entry.</summary>
public sealed record ScheduleEntry
{
    /// <summary>Unique identifier for this schedule.</summary>
    public required ScheduleId Id { get; init; }

    /// <summary>The intent (natural-language instruction) to execute.</summary>
    public required string Intent { get; init; }

    /// <summary>Portfolio options for this run.</summary>
    public required PortfolioOptions Options { get; init; }

    /// <summary>When and how often to run.</summary>
    public required ScheduleExpression Expression { get; init; }

    /// <summary>UTC time this entry was last executed. Null if never run.</summary>
    public DateTimeOffset? LastRunAt { get; init; }
}

/// <summary>Identifier for a schedule entry.</summary>
public sealed record ScheduleId(string Value);