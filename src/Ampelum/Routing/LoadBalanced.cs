using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Round-robin strategy that distributes calls evenly across all configured endpoints.
/// Uses a single <see cref="System.Threading.Interlocked"/> counter so that selection
/// is allocation-free and thread-safe (PSQ-1, v0.9g).
/// </summary>
/// <remarks>
/// Endpoints other than the chosen primary are exposed as fallbacks (in their natural
/// order) so callers may still escalate on transport failure.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LoadBalanced : IRoutingStrategy
{
    long _cursor = -1;

    /// <inheritdoc/>
    public string Name => "load-balanced";

    /// <inheritdoc/>
    public RoutingDecision Select(IReadOnlyList<ProviderEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));

        var next = System.Threading.Interlocked.Increment(ref _cursor);
        // Modulo on a non-negative interlocked value: starting at -1 and incrementing yields 0,1,2â€¦
        // which cannot overflow before billions of years of contiguous traffic.
        var index = (int)((ulong)next % (ulong)endpoints.Count);
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
