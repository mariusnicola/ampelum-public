using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Safety.Tools;

/// <summary>Default in-memory implementation. Thread-safe.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class InMemoryToolRegistry : IToolRegistry
{
    readonly ConcurrentDictionary<(string Name, string Version), ToolRegistration> _map = new();

    /// <summary>Registers a tool. Throws <see cref="InvalidOperationException"/> on duplicate (name, version).</summary>
    public void Register(ToolRegistration registration)
    {
        if (!_map.TryAdd((registration.Name, registration.Version), registration))
            throw new InvalidOperationException(
                $"Tool '{registration.Name}'@'{registration.Version}' is already registered.");
    }

    /// <inheritdoc/>
    public bool TryGet(string name, string version, out ToolRegistration? registration)
    {
        if (_map.TryGetValue((name, version), out var found))
        {
            registration = found;
            return true;
        }
        registration = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerable<ToolRegistration> GetAll() => _map.Values;
}
