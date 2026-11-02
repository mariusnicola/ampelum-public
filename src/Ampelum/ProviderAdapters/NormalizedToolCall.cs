using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.ProviderAdapters;

/// <summary>
/// Provider-neutral representation of a tool / function call (PSQ-7, v0.9g). Round-trips
/// across OpenAI, Anthropic and Gemini through <see cref="ToolCallFormatConverter"/>.
/// </summary>
/// <param name="Id">Stable identifier. OpenAI / Anthropic carry a native id; Gemini synthesises one.</param>
/// <param name="Name">Tool / function name.</param>
/// <param name="Arguments">JSON-shaped argument bag. Empty dictionary represents "no arguments".</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record NormalizedToolCall(
    string Id,
    string Name,
    IReadOnlyDictionary<string, object?> Arguments);
