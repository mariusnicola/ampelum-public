namespace SignalGrid.Ampelum.Context;

/// <summary>Typed event-log store providing append, read, turn/seq counters, and writer-lock.</summary>
public interface IEventStore : IAsyncDisposable
{
    /// <summary>Appends an entry under the writer lock. Throws if lock not held.</summary>
    ValueTask AppendAsync(EventEntry entry, CancellationToken ct = default);

    /// <summary>Reads entries matching the query, ordered by turn ASC, seq ASC.</summary>
    IAsyncEnumerable<EventEntry> ReadAsync(EventQuery query, CancellationToken ct = default);

    /// <summary>Current turn counter (zero-based; incremented on each UserMessage append).</summary>
    ValueTask<int> CurrentTurnAsync(CancellationToken ct = default);

    /// <summary>Reserves the next seq for the given turn and returns it.</summary>
    ValueTask<int> NextSeqAsync(int turn, CancellationToken ct = default);

    /// <summary>Marks an event key as superseded by another key.</summary>
    ValueTask SupersedeAsync(string key, string supersededBy, CancellationToken ct = default);

    /// <summary>Forces a WAL checkpoint (SQLite) or equivalent durability flush.</summary>
    ValueTask CheckpointAsync(CancellationToken ct = default);

    /// <summary>Acquires an exclusive writer lock. Dispose to release.</summary>
    ValueTask<IAsyncDisposable> AcquireWriterLockAsync(CancellationToken ct = default);
}