using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SignalGrid.Ampelum.Host.Optimize;

/// <summary>
/// In-memory pub/sub keyed by runId. Per-(runId, subscriberId) channels for broadcast.
/// Channels are NOT GC'd in v0.7 (memory grows linearly with run count). v0.8 adds TTL eviction.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal sealed class OptimizerProgressBus : IOptimizerProgressBus
{
    // runId -> (subscriberId -> Channel)
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Channel<SseEnvelope>>> _subscribers = new();

    public void Publish(string runId, SseEnvelope envelope)
    {
        if (!_subscribers.TryGetValue(runId, out var channels)) return;
        foreach (var ch in channels.Values)
            ch.Writer.TryWrite(envelope);
    }

    public void Complete(string runId)
    {
        if (!_subscribers.TryGetValue(runId, out var channels)) return;
        foreach (var ch in channels.Values)
            ch.Writer.TryComplete();
    }

    public async IAsyncEnumerable<SseEnvelope> Subscribe(
        string runId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var subscriberId = Guid.NewGuid().ToString("N");
        var ch = Channel.CreateUnbounded<SseEnvelope>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var channels = _subscribers.GetOrAdd(runId, _ => new());
        channels[subscriberId] = ch;
        try
        {
            await foreach (var env in ch.Reader.ReadAllAsync(ct))
                yield return env;
        }
        finally
        {
            channels.TryRemove(subscriberId, out _);
        }
    }
}
