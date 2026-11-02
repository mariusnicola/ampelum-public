using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

public sealed class TraceSinksTests
{
    static TraceRecord R() => new(
        Guid.NewGuid(), "Predict", typeof(object),
        new Dictionary<string, object?>(), new Dictionary<string, object?>(),
        [], "raw", null, TimeSpan.Zero, null, new Dictionary<string, object?>());

    [Fact]
    public void Null_DoesNothing()
    {
        NullTraceSink.Instance.Write(R());
    }

    [Fact]
    public void InMemory_CapturesAndClears()
    {
        var sink = new InMemoryTraceSink();
        sink.Write(R()); sink.Write(R());
        sink.Records.Count.ShouldBe(2);
        sink.Clear();
        sink.Records.ShouldBeEmpty();
    }

    [Fact]
    public void InMemory_ThreadSafe()
    {
        var sink = new InMemoryTraceSink();
        Parallel.For(0, 1000, _ => sink.Write(R()));
        sink.Records.Count.ShouldBe(1000);
    }

    [Fact]
    public void Logging_LogsAtInformation()
    {
        var captured = new List<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger(captured);
        var sink = new LoggingTraceSink(logger);
        sink.Write(R());
        captured.Count.ShouldBe(1);
        captured[0].Level.ShouldBe(LogLevel.Information);
        // Ensure BeginScope and IsEnabled are covered
        logger.BeginScope(new { });
        logger.IsEnabled(LogLevel.Information).ShouldBeTrue();
    }

    [Fact]
    public void Logging_WithUsageDetails_LogsTokenCounts()
    {
        // Exercises the non-null branch of record.Usage?.InputTokenCount and
        // record.Usage?.OutputTokenCount in LoggingTraceSink.Write.
        var captured = new List<(LogLevel Level, string Message)>();
        var logger = new CapturingLogger(captured);
        var sink = new LoggingTraceSink(logger);
        var record = new TraceRecord(
            Guid.NewGuid(), "Predict", typeof(object),
            new Dictionary<string, object?>(), new Dictionary<string, object?>(),
            [], "raw",
            new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            TimeSpan.FromMilliseconds(100), null, new Dictionary<string, object?>());
        sink.Write(record);
        captured.Count.ShouldBe(1);
        captured[0].Message.ShouldContain("10");
        captured[0].Message.ShouldContain("5");
    }

    sealed class CapturingLogger(List<(LogLevel, string)> store) : ILogger<LoggingTraceSink>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => store.Add((level, fmt(state, ex)));
        sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
