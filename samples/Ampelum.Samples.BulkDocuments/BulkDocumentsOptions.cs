using SignalGrid.Ampelum.RateLimiting;
using SignalGrid.Ampelum.Resilience;

namespace Ampelum.Samples.BulkDocuments;

/// <summary>Tunables of the pipeline. Only the batch model has no default because it must come from configuration.</summary>
public sealed record BulkDocumentsOptions
{
    /// <summary>Model sent with the whole batch. The batch API rejects a request without one.</summary>
    public required string BatchModel { get; init; }

    public int MaxSummaryTokens { get; init; } = 256;

    /// <summary>Wait between two status checks of a running batch.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxPolls { get; init; } = 120;

    /// <summary>How many times the batch is submitted, each time only for documents still without a summary.</summary>
    public int BatchAttempts { get; init; } = 2;

    public RoutingMode Routing { get; init; } = RoutingMode.Failover;

    /// <summary>With key rotating routing, how many requests go to one endpoint before the next one takes over.</summary>
    public int KeyRotatingCallsPerKey { get; init; } = 1;

    public RateLimitingOptions RateLimit { get; init; } = new();

    public CircuitBreakerOptions Breaker { get; init; } = new();

    /// <summary>How often an unparseable interactive reply is re-asked with the parse error as hint.</summary>
    public int ParseRecoveryAttempts { get; init; } = 2;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
