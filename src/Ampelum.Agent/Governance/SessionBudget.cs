namespace SignalGrid.Ampelum.Agent;

/// <summary>Per-session budget limits. When 80% of any limit is reached,
/// MemoryManager triggers compression. At 100%, operations are blocked.</summary>
public sealed record SessionBudget
{
    /// <summary>Maximum tool calls allowed in this session. Null = unlimited.</summary>
    public long? MaxCallCount { get; init; }

    /// <summary>Maximum tokens consumed (prompt + completion). Null = unlimited.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Maximum output bytes from tool results. Null = unlimited.</summary>
    public long? MaxOutputBytes { get; init; }

    /// <summary>Maximum session duration. Null = unlimited.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>When session started — used to check duration budget.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Check if any budget is at warning threshold (80%).</summary>
    public bool IsWarning(long callCount, long tokensUsed, long outputBytes)
    {
        if (MaxCallCount.HasValue && callCount >= MaxCallCount.Value * 0.8) return true;
        if (MaxTokens.HasValue && tokensUsed >= MaxTokens.Value * 0.8) return true;
        if (MaxOutputBytes.HasValue && outputBytes >= MaxOutputBytes.Value * 0.8) return true;
        if (MaxDuration.HasValue &&
            DateTimeOffset.UtcNow - StartedAt >= MaxDuration.Value * 0.8) return true;
        return false;
    }

    /// <summary>Check if any budget is fully exhausted.</summary>
    public bool IsExhausted(long callCount, long tokensUsed, long outputBytes)
    {
        if (MaxCallCount.HasValue && callCount >= MaxCallCount.Value) return true;
        if (MaxTokens.HasValue && tokensUsed >= MaxTokens.Value) return true;
        if (MaxOutputBytes.HasValue && outputBytes >= MaxOutputBytes.Value) return true;
        if (MaxDuration.HasValue &&
            DateTimeOffset.UtcNow - StartedAt >= MaxDuration.Value) return true;
        return false;
    }
}