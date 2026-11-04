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
    TimeSpan Duration)
{
    /// <summary>
    /// Prompt files left out of the index because another file carries the same version id and was kept
    /// (the one with the newest creation date; on a tie the first by path), as paths under the prompt folder.
    /// </summary>
    public IReadOnlyList<string> PromptFilesSkipped { get; init; } = [];
}
