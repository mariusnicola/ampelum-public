using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>
/// Response body of <c>POST /modules/{moduleName}/forward</c>. Spec §5/§6.
/// <c>TraceId</c> is a W3C TraceContext-derived 32-lowercase-hex identifier (HQ-1).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ForwardResponse(
    string TraceId,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    UsageInfo Usage,
    string ModelId,
    string ModuleName,
    string? PromptVersionId);
