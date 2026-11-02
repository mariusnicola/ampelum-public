using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Persistence;

/// <summary>Snapshot store for the module registry (used for /readyz verification).</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IModuleRegistrySnapshotStore
{
    /// <summary>Inserts or replaces a module snapshot row.</summary>
    ValueTask UpsertAsync(ModuleSnapshotRow row, CancellationToken ct);

    /// <summary>Lists all module snapshot rows.</summary>
    ValueTask<IReadOnlyList<ModuleSnapshotRow>> ListAsync(CancellationToken ct);

    /// <summary>Deletes all rows (used by IndexRebuilder).</summary>
    ValueTask DeleteAllAsync(CancellationToken ct);
}

/// <summary>A row in the module registry snapshot.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ModuleSnapshotRow(
    string ModuleName,
    string SignatureType,
    bool SupportsStream,
    DateTimeOffset RegisteredAt);
