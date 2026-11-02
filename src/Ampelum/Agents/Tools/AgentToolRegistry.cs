using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace SignalGrid.Ampelum.Agents.Tools;

/// <summary>Default <see cref="IAgentToolRegistry"/> implementation backed by a concurrent dictionary.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    readonly ConcurrentDictionary<(string Name, string Version), IAgentTool> _tools = new();

    /// <inheritdoc/>
    public void Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var key = (tool.Name, tool.Version);
        if (!_tools.TryAdd(key, tool))
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' version '{tool.Version}' already registered.");
    }

    /// <inheritdoc/>
    public IAgentTool? Find(string name, string version)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);
        return _tools.TryGetValue((name, version), out var tool) ? tool : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IAgentTool> ListByCategory(string category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return [.. _tools.Values.Where(t => string.Equals(t.Category, category, StringComparison.Ordinal))];
    }

    /// <inheritdoc/>
    public IReadOnlyList<IAgentTool> All() => [.. _tools.Values];
}
