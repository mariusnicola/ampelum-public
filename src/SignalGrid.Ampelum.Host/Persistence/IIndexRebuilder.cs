using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Rebuilds all SQLite read-side indexes from authoritative sources.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IIndexRebuilder
{
    /// <summary>Rebuilds all indexes. Concurrent calls are serialized.</summary>
    ValueTask<RebuildSummary> RebuildAsync(CancellationToken ct);
}

/// <summary>Summary of a completed rebuild operation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record RebuildSummary(
    int AuditEntriesIndexed,
    int PromptVersionsIndexed,
    int OptimizerRunsCleared,
    int ModulesSnapshot,
    TimeSpan Duration);
