using Xunit;
using System.Collections.Concurrent;
using System.Diagnostics;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Observability.Phoenix;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ampelum.Observability.Phoenix.Tests;

public sealed class PhoenixExporterTests
{
    sealed class CapturingSink : ITraceSink
    {
        public List<TraceRecord> Written { get; } = new();
        public Exception? Throw { get; set; }
        public void Write(TraceRecord r) { if (Throw is not null) throw Throw; Written.Add(r); }
    }

    sealed class ActivityCapture : IDisposable
    {
        public ConcurrentBag<Activity> Stopped { get; } = new();
        readonly ActivityListener _listener;
        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == PhoenixActivitySource.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = a => Stopped.Add(a),
            };
            ActivitySource.AddActivityListener(_listener);
        }
        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void Write_ForwardsToInnerThenEmitsActivity()
    {
        using var capture = new ActivityCapture();
        var inner = new CapturingSink();
        var exp = new PhoenixExporter(inner, new PhoenixSpanMapper(), Options.Create(new PhoenixExporterOptions()));
        var rec = TraceRecordBuilder.Sample();
        exp.Write(rec);
        inner.Written.ShouldContain(rec);
        capture.Stopped.Count.ShouldBe(1);
        var act = capture.Stopped.Single();
        act.OperationName.ShouldBe("Predict");
        act.GetTagItem("ampelum.module").ShouldBe("Predict");
    }

    [Fact]
    public void Write_SwallowsInnerSinkException()
    {
        using var capture = new ActivityCapture();
        var inner = new CapturingSink { Throw = new InvalidOperationException("x") };
        var exp = new PhoenixExporter(inner, new PhoenixSpanMapper(), Options.Create(new PhoenixExporterOptions()));
        Should.NotThrow(() => exp.Write(TraceRecordBuilder.Sample()));
        capture.Stopped.Count.ShouldBe(1);
    }

    [Fact]
    public void Write_NoOpWhenNoListener()
    {
        var inner = new CapturingSink();
        var exp = new PhoenixExporter(inner, new PhoenixSpanMapper(), Options.Create(new PhoenixExporterOptions()));
        Should.NotThrow(() => exp.Write(TraceRecordBuilder.Sample()));
        inner.Written.Count.ShouldBe(1);
    }

    [Fact]
    public void Write_ThrowsOnNull()
    {
        var exp = new PhoenixExporter(new CapturingSink(), new PhoenixSpanMapper(),
            Options.Create(new PhoenixExporterOptions()));
        Should.Throw<ArgumentNullException>(() => exp.Write(null!));
    }

    [Fact]
    public void Constructor_ValidatesArgs()
    {
        var inner = new CapturingSink();
        var mapper = new PhoenixSpanMapper();
        var opts = Options.Create(new PhoenixExporterOptions());
        Should.Throw<ArgumentNullException>(() => new PhoenixExporter(null!, mapper, opts));
        Should.Throw<ArgumentNullException>(() => new PhoenixExporter(inner, null!, opts));
        Should.Throw<ArgumentNullException>(() => new PhoenixExporter(inner, mapper, null!));
    }
}
