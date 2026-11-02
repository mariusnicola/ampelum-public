using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SignalGrid.Ampelum.Context;

/// <summary>In-memory <see cref="IEventStore"/> for testing. Not durable; single-process only.</summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly List<EventEntry> _entries = [];
    private readonly SemaphoreSlim _writerSem = new(1, 1);
    private int _turnCounter;
    private int _nextSeq;

    /// <inheritdoc />
    public ValueTask<IAsyncDisposable> AcquireWriterLockAsync(CancellationToken ct = default)
    {
        if (!_writerSem.Wait(0, ct))
            throw new InvalidOperationException("Writer lock already held by another caller in this process.");
        return ValueTask.FromResult<IAsyncDisposable>(new WriterRelease(_writerSem));
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(EventEntry entry, CancellationToken ct = default)
    {
        if (_writerSem.CurrentCount != 0)
            throw new InvalidOperationException("AppendAsync requires writer lock.");
        _entries.Add(entry);
        if (entry is UserMessage)
            Interlocked.Increment(ref _turnCounter);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EventEntry> ReadAsync(EventQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filtered = _entries.AsEnumerable();
        if (query.SinceTurn is int s) filtered = filtered.Where(e => e.Turn > s);
        if (query.UntilTurn is int u) filtered = filtered.Where(e => e.Turn <= u);
        if (!query.IncludeSuperseded) filtered = filtered.Where(e => e.SupersededBy is null);
        if (query.Types is { Length: > 0 })
        {
            var typeNames = new HashSet<string>(query.Types);
            filtered = filtered.Where(e => typeNames.Contains(e.GetType().Name));
        }
        foreach (var e in filtered.OrderBy(e => e.Turn).ThenBy(e => e.Seq))
        {
            yield return e;
        }
        await ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> CurrentTurnAsync(CancellationToken ct = default)
        => ValueTask.FromResult(Volatile.Read(ref _turnCounter));

    /// <inheritdoc />
    public ValueTask<int> NextSeqAsync(int turn, CancellationToken ct = default)
        => ValueTask.FromResult(Interlocked.Increment(ref _nextSeq) - 1);

    /// <inheritdoc />
    public ValueTask SupersedeAsync(string key, string supersededBy, CancellationToken ct = default)
    {
        var entry = _entries.FirstOrDefault(e => e.Key == key);
        if (entry is not null)
        {
            var idx = _entries.IndexOf(entry);
            // Create new entry with SupersededBy set (records are immutable)
            _entries[idx] = entry with { SupersededBy = supersededBy };
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask CheckpointAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() { _writerSem.Dispose(); return ValueTask.CompletedTask; }

    private sealed class WriterRelease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sem;
        public WriterRelease(SemaphoreSlim s) { _sem = s; }
        public ValueTask DisposeAsync() { _sem.Release(); return ValueTask.CompletedTask; }
    }
}