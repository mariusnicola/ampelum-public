using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Strategy that rotates through a fixed list of API-key-bearing endpoints, advancing
/// after a configurable burst of calls per endpoint. Useful for spreading load across
/// per-key rate limits (PSQ-1, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class KeyRotating : IRoutingStrategy
{
    readonly int _callsPerKey;
    long _counter = -1;

    /// <summary>Creates a new key-rotating strategy.</summary>
    /// <param name="callsPerKey">Number of calls served by one endpoint before rotating.
    /// Must be at least 1; defaults to 1 (rotate every call).</param>
    public KeyRotating(int callsPerKey = 1)
    {
        if (callsPerKey < 1)
            throw new ArgumentOutOfRangeException(nameof(callsPerKey), callsPerKey, "Must be >= 1.");
        _callsPerKey = callsPerKey;
    }

    /// <inheritdoc/>
    public string Name => "key-rotating";

    /// <inheritdoc/>
    public RoutingDecision Select(IReadOnlyList<ProviderEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));

        var next = System.Threading.Interlocked.Increment(ref _counter);
        // Bucket every _callsPerKey invocations onto the same endpoint, then rotate.
        var bucket = (ulong)next / (ulong)_callsPerKey;
        var index = (int)(bucket % (ulong)endpoints.Count);
        var primary = endpoints[index];

        if (endpoints.Count == 1)
            return new RoutingDecision(primary, Array.Empty<ProviderEndpoint>());

        var fallbacks = new ProviderEndpoint[endpoints.Count - 1];
        var write = 0;
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (i == index) continue;
            fallbacks[write++] = endpoints[i];
        }
        return new RoutingDecision(primary, fallbacks);
    }
}
