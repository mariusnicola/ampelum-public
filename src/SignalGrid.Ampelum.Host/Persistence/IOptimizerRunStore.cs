using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Read-side index of optimizer run state.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IOptimizerRunStore
{
    /// <summary>Inserts a new optimizer run in queued state.</summary>
    ValueTask InsertAsync(OptimizerRunRow row, CancellationToken ct);

    /// <summary>Updates progress; transitions queued -> running automatically.</summary>
    ValueTask UpdateProgressAsync(string runId, int trialsCompleted, int? trialsTotal, CancellationToken ct);

    /// <summary>Completes a running run with the given final state.</summary>
    ValueTask CompleteAsync(string runId, string finalState, string? resultVersionId, string? errorMessage, CancellationToken ct);

    /// <summary>Gets a run by ID; returns null if not found.</summary>
    ValueTask<OptimizerRunRow?> TryGetAsync(string runId, CancellationToken ct);

    /// <summary>Deletes all rows (used by IndexRebuilder).</summary>
    ValueTask DeleteAllAsync(CancellationToken ct);
}

/// <summary>A row in the optimizer runs read-side index.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OptimizerRunRow(
    string RunId,
    string ModuleName,
    string OptimizerType,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int TrialsCompleted,
    int? TrialsTotal,
    string? ResultVersionId,
    string? ErrorMessage);
