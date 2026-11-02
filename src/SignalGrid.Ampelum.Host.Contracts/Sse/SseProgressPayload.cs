using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>progress</c> event on the optimize stream. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseProgressPayload(string RunId, int TrialsCompleted, int? TrialsTotal);
