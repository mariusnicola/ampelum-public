using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Thread-safe, insertion-ordered registry of <see cref="ModuleSlot"/> instances.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModuleRegistry : IModuleRegistry
{
    readonly object _gate = new();
    readonly Dictionary<string, ModuleSlot> _byName = new(StringComparer.Ordinal);
    readonly List<string> _order = [];
    bool _frozen;

    /// <inheritdoc/>
    public void Register(ModuleSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (_gate)
        {
            if (_frozen) throw new InvalidOperationException(
                "ModuleRegistry is frozen — register modules before Freeze().");
            if (!_byName.TryAdd(slot.ModuleName, slot))
                throw new InvalidOperationException(
                    $"Module '{slot.ModuleName}' already registered.");
            _order.Add(slot.ModuleName);
        }
    }

    /// <inheritdoc/>
    public void Freeze() { lock (_gate) _frozen = true; }

    /// <inheritdoc/>
    public bool IsFrozen { get { lock (_gate) return _frozen; } }

    /// <inheritdoc/>
    public int Count { get { lock (_gate) return _order.Count; } }

    /// <inheritdoc/>
    public IReadOnlyList<string> Names { get { lock (_gate) return _order.ToArray(); } }

    /// <inheritdoc/>
    public ModuleSlot Get(string moduleName)
    {
        if (TryGet(moduleName, out var slot)) return slot;
        throw new KeyNotFoundException($"Module '{moduleName}' not registered.");
    }

    /// <inheritdoc/>
    public bool TryGet(string moduleName, out ModuleSlot slot)
    {
        lock (_gate) return _byName.TryGetValue(moduleName, out slot!);
    }
}
