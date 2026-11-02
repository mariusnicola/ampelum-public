using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Tools;

namespace SignalGrid.Ampelum.Agents.Tools;

/// <summary>
/// A typed tool usable by agents in a multi-agent system. Extends <see cref="ITypedTool{TResult}"/>
/// with categorisation and version metadata for registry lookup.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAgentTool : ITypedTool<AgentToolResult>
{
    /// <summary>Free-form category used by <c>IAgentToolRegistry.ListByCategory</c>.</summary>
    string Category { get; }

    /// <summary>Version string; unique per <see cref="ITypedTool{TResult}.Name"/> within a registry.</summary>
    string Version { get; }
}
