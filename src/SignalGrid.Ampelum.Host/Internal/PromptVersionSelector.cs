#pragma warning disable AMPELUM_V09_PREVIEW
using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Optimizers;

namespace SignalGrid.Ampelum.Host.Internal;

/// <summary>
/// Picks the prompt version a forward or stream call uses and applies its instruction. The registered module is a
/// single Predict under the submodule name "default", the key a prompt version's instruction overrides use.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class PromptVersionSelector
{
    const string DefaultSubmodule = "default";

    /// <summary>
    /// The version the caller names, or else the module's active one (null when there is none).
    /// <c>Missing</c> is true when a named version does not exist or belongs to another module.
    /// </summary>
    public static async Task<(PromptVersionRow? Version, bool Missing)> FindAsync(
        IPromptVersionStore store, string moduleName, string? wantedVersionId, CancellationToken ct)
    {
        if (wantedVersionId is null)
            return (await store.GetActiveAsync(moduleName, ct), false);

        var row = await store.TryGetByIdAsync(wantedVersionId, ct);
        return row is not null && row.ModuleName == moduleName ? (row, false) : (null, true);
    }

    /// <summary>The version's instruction for the "default" submodule, or null when it has none. Throws when the file cannot be read.</summary>
    public static async Task<string?> InstructionAsync(PromptVersionRow version, CancellationToken ct)
    {
        var shape = await PromptVersionFileReader.ReadAsync(version.FilePath, ct);
        return shape.InstructionOverrides.TryGetValue(DefaultSubmodule, out var instruction)
            && !string.IsNullOrWhiteSpace(instruction)
            ? instruction
            : null;
    }

    /// <summary>A Predict rebound for one call with <paramref name="instruction"/> as its instruction. The given Predict is unchanged.</summary>
    public static Predict<TSig> Apply<TSig>(Predict<TSig> inner, string instruction) where TSig : ISignature
    {
        var context = ((IBoundContext)inner).BoundContext
            ?? AmpelumContext.Ambient
            ?? throw new InvalidOperationException("The module has no AmpelumContext to apply the prompt version to.");
        var name = context.SubmoduleName ?? DefaultSubmodule;
        return inner.WithContext(context with
        {
            SubmoduleName = name,
            InstructionOverrides = new Dictionary<string, string> { [name] = instruction },
        });
    }
}
