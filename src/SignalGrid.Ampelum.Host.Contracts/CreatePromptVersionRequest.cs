using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Request body of <c>POST /prompts/{moduleName}</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record CreatePromptVersionRequest(
    IReadOnlyDictionary<string, IReadOnlyList<DemoExample>> DemoAssignments,
    IReadOnlyDictionary<string, string> InstructionOverrides,
    string? ParentVersionId = null,
    string? Note = null);
