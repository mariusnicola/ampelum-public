using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Usage;

/// <summary>
/// Scoped service that accumulates token and cost data across a logical operation.
/// Implementations MUST be thread-safe â€” <see cref="Evaluate{TSig}"/> calls modules concurrently.
/// </summary>
public interface IUsageTracker
{
    /// <summary>Records one LM request. <paramref name="usage"/> may be <see langword="null"/>
    /// when the provider does not return usage details; in that case <see cref="UsageSummary.RequestCount"/>
    /// still increments but token counts do not.</summary>
    void Record(UsageDetails? usage, bool cacheHit);

    /// <summary>Returns totals accumulated since construction or the last <see cref="Reset"/>.</summary>
    UsageSummary Snapshot();

    /// <summary>Resets all accumulated totals to zero. Does not affect open scopes.</summary>
    void Reset();

    /// <summary>Opens a named scope for attribution. Calls inside the scope are counted under
    /// <paramref name="name"/> in <see cref="UsageSummary.PerScope"/> in addition to globals.
    /// Scopes are nestable.</summary>
    IDisposable Scope(string name);
}

/// <summary>Immutable snapshot of accumulated usage totals.</summary>
public sealed record UsageSummary(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    int RequestCount,
    int CacheHits,
    IReadOnlyDictionary<string, UsageSummary>? PerScope);
