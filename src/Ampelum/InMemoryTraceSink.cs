using System.Collections.Concurrent;
using SignalGrid.Ampelum.Usage;

namespace SignalGrid.Ampelum;

/// <summary>
/// Thread-safe in-memory <see cref="ITraceSink"/> that accumulates all trace records for inspection.
/// Intended for testing and development; not recommended for production due to unbounded memory growth.
/// </summary>
public sealed class InMemoryTraceSink : ITraceSink, IInspectableSink
{
    readonly ConcurrentQueue<TraceRecord> _records = new();

    /// <inheritdoc/>
    public void Write(TraceRecord record) => _records.Enqueue(record);

    /// <summary>
    /// Returns a snapshot of all recorded traces in insertion order.
    /// Note: each access materialises a new array â€” avoid calling in a tight loop.
    /// </summary>
    public IReadOnlyList<TraceRecord> Records => [.. _records];

    /// <summary>Removes all previously recorded traces.</summary>
    public void Clear()
    {
        while (_records.TryDequeue(out _)) { }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TraceRecord> GetRecent(int n)
    {
        if (n <= 0) return Array.Empty<TraceRecord>();
        var all = _records.ToArray();  // ConcurrentQueue snapshot
        if (all.Length <= n) return all;
        var result = new TraceRecord[n];
        Array.Copy(all, all.Length - n, result, 0, n);
        return result;
    }
}
