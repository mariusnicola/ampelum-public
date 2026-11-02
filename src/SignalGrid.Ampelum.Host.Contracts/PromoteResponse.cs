using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Response body of <c>POST /prompts/{moduleName}/promote</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromoteResponse(
    string ModuleName,
    string ActiveVersionId,
    string? PreviousActiveVersionId);
