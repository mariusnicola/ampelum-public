using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>
/// Wire shape of a single LangSmith run. Subset of the LangSmith /runs schema
/// snapshot pinned in <see cref="LangSmithSchemaVersion.SnapshotDate"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangSmithRun
{
    /// <summary>Stable identifier for this run.</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Display name (typically the Ampelum module name).</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>One of <c>chain | llm | tool | retriever | embedding | prompt | parser</c>.</summary>
    [JsonPropertyName("run_type")]
    public required string RunType { get; init; }

    /// <summary>UTC start of the underlying ForwardAsync call.</summary>
    [JsonPropertyName("start_time")]
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>UTC end of the underlying ForwardAsync call.</summary>
    [JsonPropertyName("end_time")]
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>Optional inputs payload, populated only when <c>IncludeAuditPayload=true</c>.</summary>
    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, object?>? Inputs { get; init; }

    /// <summary>Optional outputs payload, populated only when <c>IncludeAuditPayload=true</c>.</summary>
    [JsonPropertyName("outputs")]
    public IReadOnlyDictionary<string, object?>? Outputs { get; init; }

    /// <summary>Free-form extras: <c>metadata</c>, <c>runtime</c>, etc.</summary>
    [JsonPropertyName("extra")]
    public IReadOnlyDictionary<string, object?>? Extra { get; init; }

    /// <summary>Error message (set when the underlying ForwardAsync threw).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>LangSmith project name (mirrored as a body field for legacy consumers).</summary>
    [JsonPropertyName("session_name")]
    public string? SessionName { get; init; }
}
