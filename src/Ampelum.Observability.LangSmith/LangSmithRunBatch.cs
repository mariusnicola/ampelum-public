using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>Wire shape posted to <c>/runs/batch</c>. Up to 100 runs per batch.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record LangSmithRunBatch
{
    /// <summary>Runs to ingest.</summary>
    [JsonPropertyName("post")]
    public required IReadOnlyList<LangSmithRun> Post { get; init; }
}
