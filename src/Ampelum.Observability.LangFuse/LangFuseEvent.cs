using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>Discriminated wire envelope posted to <c>/api/public/ingestion</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangFuseEvent
{
    /// <summary>Event identifier (used by LangFuse for idempotent retries).</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>One of <c>"trace-create" | "span-create" | "generation-create"</c>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Event timestamp.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Event body (typed per <see cref="Type"/>).</summary>
    [JsonPropertyName("body")]
    public required object Body { get; init; }
}

/// <summary>Wire envelope for <c>POST /api/public/ingestion</c>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangFuseIngestionRequest
{
    /// <summary>Events in this batch.</summary>
    [JsonPropertyName("batch")]
    public required IReadOnlyList<LangFuseEvent> Batch { get; init; }
}
