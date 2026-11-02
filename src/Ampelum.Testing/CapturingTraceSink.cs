using System.Collections.Concurrent;
using SignalGrid.Ampelum.Usage;

namespace SignalGrid.Ampelum.Testing;

/// <summary>
/// Thread-safe <see cref="ITraceSink"/> that records every <see cref="TraceRecord"/>
/// in insertion order. Used by <c>BootstrapFewShot</c> to capture per-submodule traces
/// during the trace-replay phase.
/// </summary>
public sealed class CapturingTraceSink : ITraceSink, IInspectableSink
{
    readonly ConcurrentQueue<TraceRecord> _records = new();

    /// <inheritdoc/>
    public void Write(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records.Enqueue(record);
    }

    /// <summary>All captured records in insertion order.</summary>
    public IReadOnlyList<TraceRecord> Records => _records.ToArray();

    /// <summary>Clears all captured records.</summary>
    public void Clear()
    {
        while (_records.TryDequeue(out _)) { }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TraceRecord> GetRecent(int n)
    {
        var all = _records.ToArray();
        return all.Skip(Math.Max(0, all.Length - n)).ToList();
    }
}
