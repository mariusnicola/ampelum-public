using System.Threading.Channels;

namespace SignalGrid.Ampelum;

/// <summary>
/// Channel-backed wrapper around an inner <see cref="ITraceSink"/> that buffers writes onto
/// a bounded queue and drains them on a single background task. Overflow drops the new
/// record (best-effort semantics for trace data) and increments
/// <see cref="Diagnostics.TraceSinkDropped"/>.
/// </summary>
/// <remarks>
/// Used as the default <see cref="ITraceSink"/> wrapper in <c>AddAmpelum</c> so that
/// trace-sink latency on the forward path is bounded regardless of how slow the inner sink
/// is. Audit data uses a separate, opt-in sink with zero-loss semantics (PSQ-3 / Task 7).
///
/// Disposal is graceful: the channel writer is completed, the drain loop processes any
/// remaining records, and only then does <see cref="DisposeAsync"/> return.
/// </remarks>
public sealed class BatchedTraceSink : ITraceSink, IAsyncDisposable, IDisposable
{
    readonly ITraceSink _inner;
    readonly Channel<TraceRecord> _channel;
    readonly Task _drainLoop;

    /// <summary>
    /// Initialises the batched sink wrapping <paramref name="inner"/> with the given
    /// queue <paramref name="capacity"/>.
    /// </summary>
    /// <param name="inner">The inner sink that physically writes records.</param>
    /// <param name="capacity">Maximum queue depth before <see cref="Write"/> drops new records.</param>
    public BatchedTraceSink(ITraceSink inner, int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "must be >= 1");
        _inner = inner;
        _channel = Channel.CreateBounded<TraceRecord>(new BoundedChannelOptions(capacity)
        {
            // FullMode.Wait: a synchronous Writer.TryWrite returns false when the queue is at
            // capacity (without blocking), which lets us count drops on the forward-call path.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _drainLoop = Task.Run(DrainAsync);
    }

    /// <inheritdoc/>
    public void Write(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_channel.Writer.TryWrite(record))
        {
            Diagnostics.TraceSinkDropped.Add(1);
        }
    }

    async Task DrainAsync()
    {
        await foreach (var record in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { _inner.Write(record); }
            catch { /* must-not-throw rule for trace path */ }
        }
    }

    /// <summary>Completes the channel and waits for the drain loop to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _drainLoop.ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous disposal path required by sync-only DI scope-dispose. Blocks the calling
    /// thread until the drain loop completes; prefer <see cref="DisposeAsync"/> in async-aware
    /// callers.
    /// </summary>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _drainLoop.GetAwaiter().GetResult();
    }
}
