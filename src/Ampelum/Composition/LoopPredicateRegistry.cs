using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Composition;

/// <summary>
/// Discoverable directory of named <see cref="LoopPredicate{T}"/> factories. Per-signature-type
/// (so registrations for <c>SigA</c> never leak into <c>SigB</c>'s namespace). Optimizers
/// enumerate <see cref="Names{T}"/>, then call <see cref="Create{T}"/> with a parameter dict
/// to instantiate. Backed by a per-T <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public static class LoopPredicateRegistry
{
    /// <summary>Per-T holder. JIT realises one closed type per <typeparamref name="T"/>; the map
    /// is reference-isolated per signature type.</summary>
    static class Holder<T> where T : ISignature
    {
        internal static readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, object?>, LoopPredicate<T>>> Map
            = new(StringComparer.Ordinal);
    }

    /// <summary>Register a factory under <paramref name="name"/> for signature type <typeparamref name="T"/>.
    /// Re-registering the same name overwrites the prior factory.</summary>
    public static void Register<T>(
        string name,
        Func<IReadOnlyDictionary<string, object?>, LoopPredicate<T>> factory)
        where T : ISignature
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        Holder<T>.Map[name] = factory;
    }

    /// <summary>Names of every registered factory for <typeparamref name="T"/>. Snapshot;
    /// safe to iterate concurrently with registrations.</summary>
    public static IReadOnlyCollection<string> Names<T>() where T : ISignature
        => Holder<T>.Map.Keys.ToArray();

    /// <summary>Construct the predicate registered under <paramref name="name"/> for
    /// <typeparamref name="T"/>, passing <paramref name="parameters"/> to its factory.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when no factory is registered under <paramref name="name"/>.</exception>
    public static LoopPredicate<T> Create<T>(
        string name,
        IReadOnlyDictionary<string, object?> parameters) where T : ISignature
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Holder<T>.Map.TryGetValue(name, out var factory))
            throw new KeyNotFoundException($"No LoopPredicate registered under '{name}' for {typeof(T).Name}.");
        return factory(parameters);
    }
}
