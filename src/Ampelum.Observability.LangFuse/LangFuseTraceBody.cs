using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>Body of a <c>trace-create</c> ingestion event.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangFuseTraceBody
{
    /// <summary>Stable identifier for this trace.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Free-form metadata.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Optional tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }
}
