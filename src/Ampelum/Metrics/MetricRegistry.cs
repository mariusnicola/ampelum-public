using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Metrics;

/// <summary>Lookup of metrics by name. Thread-safe after construction.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IMetricRegistry
{
    /// <summary>Gets a metric by its registered name. Throws <see cref="KeyNotFoundException"/> if absent.</summary>
    IMetric Get(string name);

    /// <summary>True when a metric with this name is registered. Sets <paramref name="metric"/> on hit.</summary>
    bool TryGet(string name, out IMetric metric);

    /// <summary>Snapshot of registered names.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>
/// In-memory <see cref="IMetricRegistry"/>. Constructed via <see cref="MetricRegistryBuilder"/>;
/// the registry itself is read-only after build for lock-free concurrent reads.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MetricRegistry : IMetricRegistry
{
    readonly IReadOnlyDictionary<string, IMetric> _entries;

    /// <summary>Initialises a new <see cref="MetricRegistry"/> from the given entry map.</summary>
    /// <param name="entries">The name â†’ metric map. Must not be null.</param>
    public MetricRegistry(IReadOnlyDictionary<string, IMetric> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
    }

    /// <inheritdoc/>
    public IMetric Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_entries.TryGetValue(name, out var m)) return m;
        throw new KeyNotFoundException(
            $"No metric registered with name '{name}'. Registered: [{string.Join(", ", _entries.Keys)}].");
    }

    /// <inheritdoc/>
    public bool TryGet(string name, out IMetric metric)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_entries.TryGetValue(name, out var m)) { metric = m; return true; }
        metric = null!;
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_entries.Keys;
}

/// <summary>Mutable builder for <see cref="MetricRegistry"/>. Not thread-safe.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class MetricRegistryBuilder
{
    readonly Dictionary<string, IMetric> _entries = new(StringComparer.Ordinal);

    /// <summary>Registers a metric using its <see cref="INamedMetric.Name"/>. Throws if duplicate.</summary>
    public MetricRegistryBuilder Add(INamedMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        if (!_entries.TryAdd(metric.Name, metric))
            throw new ArgumentException($"Metric '{metric.Name}' is already registered.", nameof(metric));
        return this;
    }

    /// <summary>Registers a metric under an explicit name. Throws if duplicate.</summary>
    public MetricRegistryBuilder Add(string name, IMetric metric)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(metric);
        if (!_entries.TryAdd(name, metric))
            throw new ArgumentException($"Metric '{name}' is already registered.", nameof(name));
        return this;
    }

    /// <summary>Builds an immutable <see cref="IMetricRegistry"/>.</summary>
    public IMetricRegistry Build() => new MetricRegistry(_entries);
}
