using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Payload for SSE <c>final</c> event on the forward stream. Spec §7.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SseFinalPayload(
    string TraceId,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    UsageInfo Usage,
    string ModelId,
    string? PromptVersionId);
