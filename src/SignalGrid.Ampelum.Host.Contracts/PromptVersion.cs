using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>A versioned prompt snapshot stored by <c>JsonProgramPersistence</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptVersion(
    string VersionId,
    string ModuleName,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyDictionary<string, IReadOnlyList<DemoExample>> DemoAssignments,
    IReadOnlyDictionary<string, string> InstructionOverrides,
    string? ParentVersionId,
    string? Note);
