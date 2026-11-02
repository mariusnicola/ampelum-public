using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Response body of <c>GET /optimize/runs/{runId}</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OptimizeRunStatus(
    string RunId,
    string ModuleName,
    string OptimizerType,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int TrialsCompleted,
    int? TrialsTotal,
    string? ResultPromptVersionId,
    string? Error);
