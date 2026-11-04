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

    /// <summary>
    /// What the version changes for the "default" submodule: its instruction and its demonstrations, each null when the
    /// version has none. Throws when the file cannot be read.
    /// </summary>
    public static async Task<VersionContent?> LoadAsync(PromptVersionRow version, CancellationToken ct)
    {
        var shape = await PromptVersionFileReader.ReadAsync(version.FilePath, ct);
        var instruction = shape.InstructionOverrides.TryGetValue(DefaultSubmodule, out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
        // A file written before demonstrations were stored in camel case has examples the reader sees as empty: they are left out.
        var demos = shape.DemoAssignments.TryGetValue(DefaultSubmodule, out var shapes)
            ? shapes.Where(s => s.Inputs.Count > 0 || s.Outputs.Count > 0)
                .Select(s => new Example(JsonElementUnboxer.Unbox(s.Inputs), JsonElementUnboxer.Unbox(s.Outputs))).ToList()
            : [];
        return instruction is null && demos.Count == 0
            ? null
            : new VersionContent(instruction, demos.Count > 0 ? demos : null);
    }

    /// <summary>A Predict rebound for one call with the version's instruction and demonstrations. The given Predict is unchanged.</summary>
    public static Predict<TSig> Apply<TSig>(Predict<TSig> inner, VersionContent content) where TSig : ISignature
    {
        var context = ((IBoundContext)inner).BoundContext
            ?? AmpelumContext.Ambient
            ?? throw new InvalidOperationException("The module has no AmpelumContext to apply the prompt version to.");
        var name = context.SubmoduleName ?? DefaultSubmodule;
        var bound = context with { SubmoduleName = name };
        if (content.Instruction is not null)
            bound = bound with { InstructionOverrides = new Dictionary<string, string> { [name] = content.Instruction } };
        if (content.Demos is not null)
            bound = bound with { DefaultDemos = content.Demos };
        return inner.WithContext(bound);
    }
}

/// <summary>The parts of a prompt version a call applies. A caller's own demos still win over <see cref="Demos"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed record VersionContent(string? Instruction, IReadOnlyList<Example>? Demos);
