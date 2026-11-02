using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Response body of <c>GET /modules</c>. Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record ModuleListResponse(IReadOnlyList<ModuleDescriptor> Modules);
