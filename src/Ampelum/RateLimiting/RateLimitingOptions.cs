using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.RateLimiting;

/// <summary>
/// Configuration record for <see cref="RateLimitedChatClient"/> (PSQ-3, v0.9g).
/// Controls partitioned token-bucket parameters and the acquire timeout.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RateLimitingOptions
{
    /// <summary>Sustained requests-per-minute permitted on each partition. Must be &gt; 0.</summary>
    public int RequestsPerMinute { get; init; } = 60;

    /// <summary>Burst multiplier applied to the bucket size. Default 5 (P3 5x burst tolerance).</summary>
    public double BurstMultiplier { get; init; } = 5.0;

    /// <summary>Maximum time to await a permit before throwing <c>RateLimitedException</c>.</summary>
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
