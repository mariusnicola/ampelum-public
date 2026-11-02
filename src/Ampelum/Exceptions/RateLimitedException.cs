using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Exceptions;

/// <summary>
/// Thrown by <c>RateLimitedChatClient</c> when a permit cannot be acquired
/// from the underlying token bucket within the configured acquire timeout
/// (PSQ-3, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class RateLimitedException : AmpelumException
{
    /// <summary>The partition key that was rate-limited.</summary>
    public string PartitionKey { get; }

    /// <summary>The acquire timeout that was reached without obtaining a permit.</summary>
    public TimeSpan WaitTime { get; }

    /// <summary>Initializes a new <see cref="RateLimitedException"/>.</summary>
    /// <param name="partitionKey">The partition key that was rate-limited.</param>
    /// <param name="waitTime">The acquire timeout that was reached.</param>
    public RateLimitedException(string partitionKey, TimeSpan waitTime)
        : base($"Rate limit exceeded for partition '{partitionKey}'; waited {waitTime.TotalMilliseconds:0} ms without acquiring a permit.")
    {
        PartitionKey = partitionKey;
        WaitTime = waitTime;
    }
}
