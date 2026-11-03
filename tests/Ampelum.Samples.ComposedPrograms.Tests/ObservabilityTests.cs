using System.Diagnostics.Metrics;
using Ampelum.Samples.ComposedPrograms.Fake;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>Trace sinks, the log hook with its GenAI scope, and exporter failure counting.</summary>
public sealed class ObservabilityTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string TempFile() => Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.jsonl");

    static async Task RunAsync(ReleaseNotesPipeline pipeline) => await pipeline.RunAsync(Ct);

    [Fact]
    public async Task The_queued_sink_delivers_every_record_to_memory_and_file_once_it_is_drained()
    {
        var path = TempFile();
        try
        {
            var pipeline = new ReleaseNotesPipeline(
                ReleaseScript.Create(), Rig.Source(), Rig.Options(o => o with { QueuedTracing = true, TracePath = path }));
            await RunAsync(pipeline);
            await pipeline.DisposeAsync();

            var records = pipeline.Traces.Records.Count;
            records.ShouldBeGreaterThan(40);
            File.ReadAllLines(path).Length.ShouldBe(records);
        }
        finally
        {
            File.Delete(path);
        }
    }

    sealed class BlockedSink : SignalGrid.Ampelum.ITraceSink
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public int Delivered;

        public void Write(SignalGrid.Ampelum.TraceRecord record)
        {
            // Bounded, so a broken queue fails the test instead of hanging it.
            Release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Increment(ref Delivered);
        }
    }

    [Fact]
    public async Task A_blocked_target_does_not_block_the_writer_and_gets_the_record_once_drained()
    {
        var target = new BlockedSink();
        var queue = Ampelum.Samples.ComposedPrograms.Observability.QueuedTraceSink.Over(target);

        queue.Write(Helpers.Trace());
        var deliveredWhenWriteReturned = Volatile.Read(ref target.Delivered);
        target.Release.Set();
        await queue.DisposeAsync();

        deliveredWhenWriteReturned.ShouldBe(0);
        target.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task The_synchronous_sink_has_written_every_record_before_the_run_returns()
    {
        var path = TempFile();
        try
        {
            await using var pipeline = new ReleaseNotesPipeline(
                ReleaseScript.Create(), Rig.Source(), Rig.Options(o => o with { QueuedTracing = false, TracePath = path }));
            await RunAsync(pipeline);

            var records = pipeline.Traces.Records.Count;
            records.ShouldBeGreaterThan(40);
            File.ReadAllLines(path).Length.ShouldBe(records);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Each_trace_line_names_the_module_and_whether_it_failed()
    {
        var path = TempFile();
        try
        {
            var pipeline = new ReleaseNotesPipeline(
                ReleaseScript.Create(), Rig.Source(), Rig.Options(o => o with { QueuedTracing = false, TracePath = path }));
            await RunAsync(pipeline);
            await pipeline.DisposeAsync();

            var lines = File.ReadAllLines(path);
            lines.ShouldContain(l => l.Contains("\"module\":\"Predict\"") && l.Contains("\"signature\":\"ClassifySig\""));
            lines.ShouldAllBe(l => l.Contains("\"failed\":false"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_logging_sink_writes_a_log_entry_for_trace_records()
    {
        var loggers = new RecordingLoggers();
        var pipeline = new ReleaseNotesPipeline(
            ReleaseScript.Create(), Rig.Source(), Rig.Options(o => o with { QueuedTracing = false }), loggers);
        await RunAsync(pipeline);
        await pipeline.DisposeAsync();

        loggers.Entries.ShouldContain(e => e.Category.Contains("LoggingTraceSink"));
    }

    [Fact]
    public async Task The_log_hook_logs_each_model_call_inside_a_scope_with_the_genai_attributes()
    {
        var loggers = new RecordingLoggers();
        await using var pipeline = new ReleaseNotesPipeline(ReleaseScript.Create(), Rig.Source(), Rig.Options(), loggers);
        await RunAsync(pipeline);

        loggers.Entries.Count(e => e.Message.StartsWith("Model call starts in")).ShouldBeGreaterThan(40);
        loggers.Scopes
            .Where(s => s.Category.Contains("GenAiLogHook"))
            .Select(s => s.State)
            .OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .ShouldContain(state => state.Any(pair => pair.Key.StartsWith("gen_ai.")));
    }

    [Fact]
    public async Task A_trace_file_that_cannot_be_written_is_counted_and_never_breaks_the_run()
    {
        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "SignalGrid.Ampelum.exporter.failures")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "exporter" && Equals(tag.Value, "release-trace-file"))
                    Interlocked.Add(ref failures, value);
        });
        listener.Start();
        // A directory path cannot be opened for appending.
        var directory = Path.Combine(Path.GetTempPath(), $"trace-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var pipeline = new ReleaseNotesPipeline(
                ReleaseScript.Create(), Rig.Source(), Rig.Options(o => o with { QueuedTracing = false, TracePath = directory }));

            var result = await pipeline.RunAsync(Ct);

            result.Notes.Count.ShouldBe(8);
            Interlocked.Read(ref failures).ShouldBeGreaterThan(40);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}
