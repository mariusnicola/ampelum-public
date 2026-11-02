using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>token</c> event. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseTokenPayload(string TraceId, string Field, string Delta);
