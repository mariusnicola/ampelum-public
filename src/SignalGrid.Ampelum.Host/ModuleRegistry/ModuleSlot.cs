using System.Diagnostics.CodeAnalysis;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Host.Contracts;

namespace SignalGrid.Ampelum.Host.ModuleRegistry;

/// <summary>Thread-safe holder for a single module's active IComposedModule instance.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class ModuleSlot
{
    object _current;

    /// <summary>The module's registered name.</summary>
    public string ModuleName { get; }

    /// <summary>Immutable descriptor built from the signature type at registration time.</summary>
    public ModuleDescriptor Descriptor { get; }

    /// <summary>Whether this module supports streaming.</summary>
    public bool SupportsStreaming => Descriptor.SupportsStreaming;

    /// <summary>Initializes a new slot with the given initial module and descriptor.</summary>
    public ModuleSlot(string moduleName, IComposedModule initial, ModuleDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(descriptor);
        ModuleName = moduleName;
        Descriptor = descriptor;
        _current = initial;
    }

    /// <summary>Gets the current module instance via a volatile read.</summary>
    public IComposedModule Current => (IComposedModule)Volatile.Read(ref _current);

    /// <summary>Atomically replaces the current module and returns the previous instance.</summary>
    public IComposedModule SwapTo(IComposedModule next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return (IComposedModule)Interlocked.Exchange(ref _current, next);
    }
}
