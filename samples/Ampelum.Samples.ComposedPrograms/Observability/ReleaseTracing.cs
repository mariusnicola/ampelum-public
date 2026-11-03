using System.Text.Json;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Observability;

namespace Ampelum.Samples.ComposedPrograms.Observability;

/// <summary>
/// Appends one JSON line per trace record to a file. A failed write is counted in the shared exporter
/// diagnostics and never reaches the pipeline, because tracing must not break a release run.
/// </summary>
public sealed class TraceFileExporter(string? path) : ITraceSink
{
    public const string ExporterName = "release-trace-file";

    readonly object _gate = new();

    public void Write(TraceRecord record)
    {
        if (path is null)
            return;
        var line = JsonSerializer.Serialize(new
        {
            module = record.ModuleName,
            signature = record.SignatureType.Name,
            durationMs = record.Duration.TotalMilliseconds,
            failed = record.Error is not null,
        });
        try
        {
            lock (_gate)
                File.AppendAllText(path, line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExporterDiagnostics.RecordExporterFailure(ExporterName, ex);
        }
    }
}

/// <summary>Writes every record to memory, to the log and to the trace file.</summary>
public class FanOutTraceSink(InMemoryTraceSink memory, ILogger<LoggingTraceSink> logger, TraceFileExporter exporter) : ITraceSink
{
    readonly ITraceSink[] _targets = [memory, new LoggingTraceSink(logger), exporter];

    public virtual void Write(TraceRecord record)
    {
        foreach (var target in _targets)
            target.Write(record);
    }
}

/// <summary>Fans out through a queue, so a slow target cannot slow a model call.</summary>
public sealed class QueuedTraceSink : ITraceSink, IAsyncDisposable
{
    readonly BatchedTraceSink _queue;
    readonly ITraceSink _entry;

    public QueuedTraceSink(InMemoryTraceSink memory, ILogger<LoggingTraceSink> logger, TraceFileExporter exporter)
        : this(new FanOutTraceSink(memory, logger, exporter))
    {
    }

    QueuedTraceSink(ITraceSink target)
    {
        _queue = new BatchedTraceSink(target);
        _entry = _queue;
    }

    /// <summary>A queue in front of any target.</summary>
    public static QueuedTraceSink Over(ITraceSink target) => new(target);

    public void Write(TraceRecord record) => _entry.Write(record);

    /// <summary>Drains the queue; records written before this call are delivered when it returns.</summary>
    public ValueTask DisposeAsync() => _queue.DisposeAsync();
}

/// <summary>Logs each model call inside a scope that carries the GenAI attributes of the running activity.</summary>
public sealed class GenAiLogHook(ILogger logger) : IHook
{
    public int Order => 10;

    public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
    {
        using (OtelLogsBridge.BeginGenAiScope(logger))
            logger.LogInformation("Model call starts in {Module}", ctx.ModuleName);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
}
