using SignalGrid.Ampelum;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>Writes each trace to every destination sink and counts the traces.</summary>
public sealed class FanOutTraceSink(IReadOnlyList<ITraceSink> sinks) : ITraceSink
{
    int _count;

    /// <summary>Traces written so far.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <inheritdoc />
    public void Write(TraceRecord record)
    {
        Interlocked.Increment(ref _count);
        foreach (var sink in sinks)
            sink.Write(record);
    }
}
