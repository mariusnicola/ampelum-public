using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>error</c> event. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseErrorPayload(string TraceId, string Code, string Message);
