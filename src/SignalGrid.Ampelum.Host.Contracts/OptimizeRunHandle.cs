using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.Contracts;

/// <summary>Response body of <c>POST /optimize/{moduleName}</c> (202 Accepted). Spec §6.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record OptimizeRunHandle(string RunId, string ModuleName);
