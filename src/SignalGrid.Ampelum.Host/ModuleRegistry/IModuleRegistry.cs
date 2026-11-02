using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Process-wide registry of <see cref="ModuleSlot"/> instances, keyed by module name.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IModuleRegistry
{
    /// <summary>Registers a module slot. Throws if frozen or name is duplicate.</summary>
    void Register(ModuleSlot slot);

    /// <summary>Freezes the registry; subsequent <see cref="Register"/> calls throw.</summary>
    void Freeze();

    /// <summary>Returns true when <see cref="Freeze"/> has been called.</summary>
    bool IsFrozen { get; }

    /// <summary>Number of registered modules.</summary>
    int Count { get; }

    /// <summary>Insertion-ordered list of module names.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Returns the slot for the named module; throws <see cref="KeyNotFoundException"/> if missing.</summary>
    ModuleSlot Get(string moduleName);

    /// <summary>Tries to get a slot; returns false when missing.</summary>
    bool TryGet(string moduleName, out ModuleSlot slot);
}
