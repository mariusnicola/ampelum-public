using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Read-side index of prompt versions with atomic promotion.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IPromptVersionStore
{
    /// <summary>Lists all module names that have at least one prompt version.</summary>
    ValueTask<IReadOnlyList<string>> ListModulesAsync(CancellationToken ct);

    /// <summary>Lists all prompt versions for the given module.</summary>
    ValueTask<IReadOnlyList<PromptVersionRow>> ListByModuleAsync(string moduleName, CancellationToken ct);

    /// <summary>Gets the active prompt version for the given module; returns null if none.</summary>
    ValueTask<PromptVersionRow?> GetActiveAsync(string moduleName, CancellationToken ct);

    /// <summary>Tries to get a prompt version by its ID; returns null if not found.</summary>
    ValueTask<PromptVersionRow?> TryGetByIdAsync(string versionId, CancellationToken ct);

    /// <summary>Inserts a new challenger prompt version.</summary>
    ValueTask InsertChallengerAsync(PromptVersionRow row, CancellationToken ct);

    /// <summary>
    /// Atomically promotes the given version to active and retires the previous active.
    /// Throws <see cref="PromoteConflictException"/> if the target is not a challenger.
    /// </summary>
    ValueTask<PromoteResult> PromoteAtomicallyAsync(string moduleName, string targetVersionId, CancellationToken ct);

    /// <summary>Deletes all rows (used by IndexRebuilder).</summary>
    ValueTask DeleteAllAsync(CancellationToken ct);
}

/// <summary>A row in the prompt versions read-side index.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptVersionRow(
    string VersionId,
    string ModuleName,
    string Status,
    DateTimeOffset CreatedAt,
    string? ParentVersionId,
    string FilePath,
    string? Note);

/// <summary>Result of an atomic promote operation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromoteResult(string NewActiveVersionId, string? PreviousActiveVersionId);

/// <summary>Thrown when a promote operation fails due to the target not being a challenger.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class PromoteConflictException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public PromoteConflictException(string message) : base(message) { }
}
