using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>Body of a <c>generation-create</c> ingestion event.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangFuseGenerationBody
{
    /// <summary>Stable identifier for this generation.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Trace identifier this generation belongs to.</summary>
    [JsonPropertyName("traceId")]
    public required string TraceId { get; init; }

    /// <summary>Display name (typically the Ampelum module name).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>UTC start time.</summary>
    [JsonPropertyName("startTime")]
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>UTC end time.</summary>
    [JsonPropertyName("endTime")]
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>Optional model identifier.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>Optional usage block.</summary>
    [JsonPropertyName("usage")]
    public LangFuseUsage? Usage { get; init; }

    /// <summary>Free-form metadata; rendered as a flattened table in the LangFuse UI.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Inputs payload (only when <c>IncludeAuditPayload=true</c>).</summary>
    [JsonPropertyName("input")]
    public object? Input { get; init; }

    /// <summary>Outputs payload (only when <c>IncludeAuditPayload=true</c>).</summary>
    [JsonPropertyName("output")]
    public object? Output { get; init; }

    /// <summary>One of <c>DEFAULT | DEBUG | WARNING | ERROR</c>.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; init; }

    /// <summary>Human-readable status; typically the exception message on error.</summary>
    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; init; }
}
