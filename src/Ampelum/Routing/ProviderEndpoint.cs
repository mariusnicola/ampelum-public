using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Routing;

/// <summary>
/// Identifies one provider endpoint that can serve LM calls inside a routed
/// <see cref="IChatClient"/> graph. The endpoint pairs a logical <see cref="Name"/>
/// with the concrete <see cref="Client"/> instance and an optional non-negative
/// <see cref="Weight"/> consumed by load-balancing strategies (PSQ-1, v0.9g).
/// </summary>
/// <param name="Name">Stable identifier propagated through audit / metadata as
/// <c>HookMetadataKeys.RouterTargetName</c>.</param>
/// <param name="Client">The underlying <see cref="IChatClient"/> that carries the call.</param>
/// <param name="Weight">Relative weight for weighted strategies (default 1.0). Strategies
/// that ignore weights (round-robin) MUST treat the value as opaque.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ProviderEndpoint(string Name, IChatClient Client, double Weight = 1.0);
