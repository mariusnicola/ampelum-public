using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Agents.Tools;

/// <summary>
/// Thread-safe registry of <see cref="IAgentTool"/>s keyed by (Name, Version) with category indexing.
/// No sandbox in v0.9a â€” execution is the agent's responsibility (PSQ-6).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IAgentToolRegistry
{
    /// <summary>Registers a tool; throws on duplicate <c>(Name, Version)</c>.</summary>
    void Register(IAgentTool tool);

    /// <summary>Returns the tool registered under <c>(name, version)</c>, or <see langword="null"/> if missing.</summary>
    IAgentTool? Find(string name, string version);

    /// <summary>Lists tools by <see cref="IAgentTool.Category"/>.</summary>
    IReadOnlyList<IAgentTool> ListByCategory(string category);

    /// <summary>Returns a snapshot of every registered tool.</summary>
    IReadOnlyList<IAgentTool> All();
}
