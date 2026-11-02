using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Error body returned on all 4xx/5xx responses. Spec §5/§6.
/// <c>TraceId</c> is W3C 32-lowercase-hex (HQ-1).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ErrorResponse(
    string TraceId,
    string Code,
    string Message,
    IReadOnlyDictionary<string, JsonElement>? Details = null);
