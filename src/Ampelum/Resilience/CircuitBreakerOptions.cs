using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Resilience;

/// <summary>
/// Configuration record for <see cref="CircuitBreakerChatClient"/> (PSQ-3 sibling, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record CircuitBreakerOptions
{
    /// <summary>Number of consecutive failures that trip the breaker into the Open state.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>How long the breaker stays in the Open state before transitioning to HalfOpen.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Predicate that decides whether an exception should count against the failure threshold.
    /// Default trips on <see cref="HttpRequestException"/> (5xx + transport) and any exception
    /// whose type name contains <c>RateLimit</c> (provider-side 429 exhaustion).
    /// </summary>
    public Func<Exception, bool> ShouldTrip { get; init; } = DefaultShouldTrip;

    static bool DefaultShouldTrip(Exception ex)
    {
        if (ex is HttpRequestException) return true;
        var name = ex.GetType().Name;
        return name.Contains("RateLimit", StringComparison.Ordinal);
    }
}
