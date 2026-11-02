using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Pluggable strategy that maps a registered set of <see cref="ProviderEndpoint"/>s
/// onto a per-call <see cref="RoutingDecision"/>. Implementations MUST be thread-safe;
/// the same instance is shared across parallel forward passes (PSQ-1, v0.9g).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IRoutingStrategy
{
    /// <summary>Stable identifier â€” surfaces in audit / telemetry.</summary>
    string Name { get; }

    /// <summary>
    /// Selects the primary endpoint plus the ordered fallback chain. Implementations
    /// are free to maintain internal state (round-robin counter, key cursor) but MUST
    /// NOT allocate per call beyond the returned <see cref="RoutingDecision"/>.
    /// </summary>
    /// <param name="endpoints">Non-empty list of registered endpoints.</param>
    RoutingDecision Select(IReadOnlyList<ProviderEndpoint> endpoints);
}
