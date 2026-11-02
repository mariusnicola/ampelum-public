using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>All prompt versions for one module from <c>GET /prompts/{moduleName}</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptVersionList(string ModuleName, IReadOnlyList<PromptVersion> Versions);
