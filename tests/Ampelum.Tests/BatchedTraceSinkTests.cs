using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;
using SignalGrid.Ampelum;

namespace Ampelum.Tests;


public sealed class BatchedTraceSinkTests
{
    static TraceRecord MakeRecord(string name = "x") =>
        new(
            Id: Guid.NewGuid(),
            ModuleName: name,
            SignatureType: typeof(BatchedTraceSinkTests),
            Inputs: new Dictionary<string, object?>(),
            Outputs: new Dictionary<string, object?>(),
            PromptMessages: Array.Empty<ChatMessage>(),
            RawResponse: string.Empty,
            Usage: null,
            Duration: TimeSpan.Zero,
            Error: null,
            Metadata: new Dictionary<string, object?>());

    [Fact]
    public async Task Append_WithinCapacity_FlushesAllRecordsOnDispose()
    {
        var inner = new InMemoryTraceSink();
        var sink = new BatchedTraceSink(inner, capacity: 1000);

        for (int i = 0; i < 50; i++)
        {
            sink.Write(MakeRecord($"r{i}"));
        }
        await sink.DisposeAsync();

        inner.Records.Count.ShouldBe(50);
    }

    [Fact]
    public async Task Append_OverflowsBoundedQueue_DropsAndIncrementsCounter()
    {
        var inner = new BlockingTraceSink();
        long droppedAfter = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == "SignalGrid.Ampelum" && instr.Name == "SignalGrid.Ampelum.trace_sink.dropped")
                l.EnableMeasurementEvents(instr);
        };
        meterListener.SetMeasurementEventCallback<long>((_, v, _, _) =>
            Interlocked.Add(ref droppedAfter, v));
        meterListener.Start();

        var sink = new BatchedTraceSink(inner, capacity: 5);
        // BlockingTraceSink prevents the drain loop from consuming, so producer hits capacity.
        for (int i = 0; i < 100; i++)
        {
            sink.Write(MakeRecord());
        }

        // Release the inner sink so DisposeAsync can drain.
        inner.Release();
        await sink.DisposeAsync();

        droppedAfter.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Dispose_FlushesPendingRecordsBeforeReturning()
    {
        var inner = new InMemoryTraceSink();
        var sink = new BatchedTraceSink(inner, capacity: 200);
        for (int i = 0; i < 100; i++) sink.Write(MakeRecord($"r{i}"));
        await sink.DisposeAsync();
        inner.Records.Count.ShouldBe(100);
    }

    [Fact]
    public void Ctor_NullInner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new BatchedTraceSink(null!));

    [Fact]
    public void Ctor_ZeroCapacity_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new BatchedTraceSink(new InMemoryTraceSink(), capacity: 0));

    [Fact]
    public void Write_NullRecord_Throws()
    {
        var sink = new BatchedTraceSink(new InMemoryTraceSink());
        Should.Throw<ArgumentNullException>(() => sink.Write(null!));
    }

    [Fact]
    public async Task InnerSinkThrows_DrainSwallowsException()
    {
        var inner = new ThrowingTraceSink();
        var sink = new BatchedTraceSink(inner, capacity: 10);
        sink.Write(MakeRecord());
        await sink.DisposeAsync();
        inner.Calls.ShouldBe(1);
    }

    sealed class BlockingTraceSink : ITraceSink
    {
        readonly ManualResetEventSlim _gate = new(false);
        public void Write(TraceRecord record)
        {
            _gate.Wait();
        }
        public void Release() => _gate.Set();
    }

    sealed class ThrowingTraceSink : ITraceSink
    {
        public int Calls;
        public void Write(TraceRecord record)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("boom");
        }
    }
}
