using System.Collections.Concurrent;
using System.Reflection;

namespace SignalGrid.Ampelum.Internal;

internal static class RecordCloner
{
    static readonly ConcurrentDictionary<Type, Func<object, object>> _cloneCache = new();
    // Keyed by Type â†’ (name â†’ PropertyInfo?); avoids a tuple allocation per property lookup.
    // Inner dict is ConcurrentDictionary so concurrent writes of different unknown keys are safe.
    static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, PropertyInfo?>> _propCache = new();

    public static T CloneWith<T>(T source, IReadOnlyDictionary<string, object?> overrides) where T : notnull
    {
        var type = source.GetType();
        var clone = _cloneCache.GetOrAdd(type, CompileClone)(source);
        var propMap = _propCache.GetOrAdd(type, BuildPropMap);
        foreach (var (name, value) in overrides)
        {
            // GetOrAdd is thread-safe: if two threads look up the same unknown key concurrently,
            // at most one reflection call is wasted â€” the winner's result is stored.
            var prop = propMap.GetOrAdd(name,
                n => type.GetProperty(n, BindingFlags.Public | BindingFlags.Instance));
            if (prop is null)
                continue; // synthetic fields (e.g., "reasoning" injected by ChainOfThought) are silently skipped
            prop.SetValue(clone, value);
        }
        return (T)clone;
    }

    static ConcurrentDictionary<string, PropertyInfo?> BuildPropMap(Type t) =>
        new(StringComparer.Ordinal);

    static Func<object, object> CompileClone(Type type)
    {
        var cloneMethod = type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Type '{type.Name}' is not a record (no <Clone>$).");
        return obj => cloneMethod.Invoke(obj, null)!;
    }
}
