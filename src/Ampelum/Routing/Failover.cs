using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Strategy that always picks the first endpoint as primary and the remaining endpoints
/// as the ordered fallback chain. The router escalates down the chain on transport failure
/// (PSQ-1, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Failover : IRoutingStrategy
{
    /// <inheritdoc/>
    public string Name => "failover";

    /// <inheritdoc/>
    public RoutingDecision Select(IReadOnlyList<ProviderEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        var fallbacks = endpoints.Count == 1
            ? Array.Empty<ProviderEndpoint>()
            : [.. endpoints.Skip(1)];
        return new RoutingDecision(endpoints[0], fallbacks);
    }
}
