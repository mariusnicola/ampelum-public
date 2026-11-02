using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>done</c> event. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseDonePayload(string TraceId);
