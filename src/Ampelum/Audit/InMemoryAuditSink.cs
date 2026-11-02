using System.Runtime.CompilerServices;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// In-memory <see cref="IAuditSink"/> for tests and dev. Thread-safe via a lock guard. Spec Â§4.5.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    readonly object _gate = new();
    readonly List<AuditEntry> _entries = [];
    long _count = 0;

    /// <inheritdoc/>
    public long NextSequence
    {
        get
        {
            lock (_gate) return _count;
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        long expected;
        lock (_gate)
        {
            expected = _count;
            if (entry.Sequence != expected)
                throw new InvalidOperationException(
                    $"Out-of-order audit entry: expected sequence {expected}, got {entry.Sequence}.");
            _entries.Add(entry);
            _count++;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AuditEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        AuditEntry[] snapshot;
        lock (_gate) snapshot = _entries.ToArray();
        foreach (var e in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
            await Task.Yield();
        }
    }
}
