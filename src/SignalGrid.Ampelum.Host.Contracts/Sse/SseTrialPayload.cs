using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>trial</c> event on the optimize stream. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseTrialPayload(string RunId, int TrialIndex, double Score, string Status);
