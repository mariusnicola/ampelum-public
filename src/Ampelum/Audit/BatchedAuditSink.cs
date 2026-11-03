using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SignalGrid.Ampelum.Audit;

/// <summary>
/// Channel-backed wrapper around an inner <see cref="IAuditSink"/> that decouples the producer
/// (audit hook on the forward path) from the inner sink's I/O latency. Uses
/// <see cref="BoundedChannelFullMode.Wait"/> for **zero-loss** semantics â€” when the queue is at
/// capacity, <see cref="AppendAsync"/> awaits available space instead of dropping. Audit-chain
/// integrity disqualifies drop-on-full from being acceptable for audit data; only trace data
/// (best-effort) uses drop semantics.
/// </summary>
/// <remarks>
/// Opt-in via <c>AmpelumBuilder.AddBatchedAuditSink&lt;TInner&gt;()</c>. The default audit registration
/// remains synchronous so the producer-to-storage contract is fully synchronous unless the user
/// explicitly accepts back-pressure on the forward path.
///
/// <see cref="NextSequence"/> is tracked on the wrapper (not delegated to inner) so the audit hook
/// can assign sequence numbers strictly without interleaving against in-flight queue entries.
/// Disposal is graceful: channel writer completion + drain await + only then return.
///
/// <para><b>When to use (v1.0-rc1.alpha.5 framing):</b></para>
/// <list type="bullet">
///   <item><b>Use batched</b> when the inner sink is I/O-bound (file flush, network, DB write)
///   AND the expected steady-state batch depth is â‰¥ 256. S6b benchmarks show the batched
///   wrapper amortises per-call sync cost and beats per-call append once both conditions
///   hold; below ~16 producer concurrency it adds queue overhead with no win.</item>
///   <item><b>Do NOT use batched</b> with an in-memory inner sink (e.g.
///   <see cref="InMemoryAuditSink"/>) â€” there is no I/O to amortise, and the channel
///   round-trip is pure overhead.</item>
///   <item><b>Do NOT use batched</b> when calling code requires read-after-write visibility:
///   <see cref="EnumerateAsync"/> reflects the inner sink only, so in-flight queue entries
///   are invisible until <see cref="DisposeAsync"/> drains.</item>
///   <item><b>Capacity sizing:</b> set <c>capacity</c> to the expected concurrent producer
///   count Ã— 4 (default 1000 fits ~250 concurrent producers). Below 16, the queue rarely
///   fills and the wrapper is not amortising â€” prefer the synchronous inner sink directly.</item>
/// </list>
/// </remarks>
public sealed class BatchedAuditSink : IAuditSink, IAsyncDisposable, ILastEntryHashSource
{
    readonly IAuditSink _inner;
    readonly Channel<AuditEntry> _channel;
    readonly Task _drainLoop;
    long _nextSequence;
    byte[]? _lastQueuedHash;

    // Queued entries are not visible through EnumerateAsync yet, so a writer that chains to the
    // newest entry must be told about them here.
    ValueTask<byte[]> ILastEntryHashSource.LastEntryHashAsync(CancellationToken ct) =>
        Volatile.Read(ref _lastQueuedHash) is { } queued
            ? ValueTask.FromResult(queued)
            : LastEntryHash.OfAsync(_inner, ct);

    /// <summary>
    /// Initialises the batched sink wrapping <paramref name="inner"/> with the given queue
    /// <paramref name="capacity"/>.
    /// </summary>
    /// <param name="inner">Inner audit sink that physically persists entries.</param>
    /// <param name="capacity">Maximum queue depth before <see cref="AppendAsync"/> awaits drain.</param>
    public BatchedAuditSink(IAuditSink inner, int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "must be >= 1");
        _inner = inner;
        _nextSequence = inner.NextSequence;
        _channel = Channel.CreateBounded<AuditEntry>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _drainLoop = Task.Run(DrainAsync);
    }

    /// <inheritdoc/>
    public long NextSequence => Interlocked.Read(ref _nextSequence);

    /// <inheritdoc/>
    public async ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // Caller assigns sequence numbers via NextSequence; we advance the counter monotonically
        // for the next caller, then await space in the channel.
        Interlocked.Increment(ref _nextSequence);
        await _channel.Writer.WriteAsync(entry, ct).ConfigureAwait(false);
        // Only an entry that was queued can be the newest one a new writer must chain to.
        Volatile.Write(ref _lastQueuedHash, entry.EntryHash);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AuditEntry> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Enumeration delegates to inner; in-flight queue entries are NOT visible until drained.
        // Callers needing a fully-consistent snapshot should DisposeAsync first.
        await foreach (var entry in _inner.EnumerateAsync(ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    async Task DrainAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await _inner.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false); }
            catch { /* must-not-throw rule on the audit-pump path */ }
        }
    }

    /// <summary>
    /// Completes the channel, waits for the drain loop to finish, then disposes the inner sink
    /// (this wrapper owns it).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _drainLoop.ConfigureAwait(false);
        if (_inner is IAsyncDisposable asyncInner)
            await asyncInner.DisposeAsync().ConfigureAwait(false);
        else if (_inner is IDisposable inner)
            inner.Dispose();
    }
}
