using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>List of all registered module names from <c>GET /prompts</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record PromptIndexResponse(IReadOnlyList<string> ModuleNames);
