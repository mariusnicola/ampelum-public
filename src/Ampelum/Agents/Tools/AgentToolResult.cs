using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agents.Tools;

/// <summary>Outcome of an <see cref="IAgentTool"/> invocation.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record AgentToolResult(
    bool Success,
    string? Output,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> Metadata);
