using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Outcome of <see cref="IRoutingStrategy.Select"/>. Carries the chosen
/// <see cref="ProviderEndpoint"/> together with the ordered fallback chain
/// the strategy is willing to attempt if the primary endpoint fails (PSQ-1, v0.9g).
/// </summary>
/// <param name="Primary">Endpoint to try first.</param>
/// <param name="Fallbacks">Ordered fallbacks. May be empty when the strategy is
/// non-failing (round-robin / key-rotating). Never <see langword="null"/>.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RoutingDecision(
    ProviderEndpoint Primary,
    IReadOnlyList<ProviderEndpoint> Fallbacks);
