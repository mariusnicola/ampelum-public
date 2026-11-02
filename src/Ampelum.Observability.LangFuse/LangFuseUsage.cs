using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>LangFuse token-usage block. Emits unitless integers; <c>unit</c> is fixed to <c>"TOKENS"</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangFuseUsage
{
    /// <summary>Input/prompt tokens.</summary>
    [JsonPropertyName("input")]
    public long? Input { get; init; }

    /// <summary>Output/completion tokens.</summary>
    [JsonPropertyName("output")]
    public long? Output { get; init; }

    /// <summary>Total tokens.</summary>
    [JsonPropertyName("total")]
    public long? Total { get; init; }

    /// <summary>Always <c>"TOKENS"</c> for this exporter.</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "TOKENS";
}
