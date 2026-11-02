using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using SignalGrid.Ampelum.Observability;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.LangFuse;

/// <summary>
/// <see cref="ITraceSink"/> decorator that mirrors every <see cref="TraceRecord"/>
/// onto the LangFuse ingestion API as a 2-event trace/generation pair.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangFuseExporter : ITraceSink, IAsyncDisposable, IDisposable
{
    readonly ITraceSink _inner;
    readonly ILangFuseClient _client;
    readonly LangFuseEventMapper _mapper;
    readonly LangFuseExporterOptions _options;
    readonly Channel<LangFuseEvent> _queue;
    readonly CancellationTokenSource _cts = new();
    readonly Task _drain;

    /// <summary>Creates a new exporter wrapping <paramref name="inner"/>.</summary>
    public LangFuseExporter(
        ITraceSink inner,
        ILangFuseClient client,
        LangFuseEventMapper mapper,
        IOptions<LangFuseExporterOptions> options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _client = client;
        _mapper = mapper;
        _options = options.Value;
        _queue = Channel.CreateBounded<LangFuseEvent>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _drain = Task.Run(DrainAsync);
    }

    /// <inheritdoc/>
    public void Write(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try { _inner.Write(record); }
        catch { /* must-not-throw */ }

        IReadOnlyList<LangFuseEvent> events;
        try { events = _mapper.Map(record, _options.IncludeAuditPayload); }
        catch (Exception ex) { ExporterDiagnostics.RecordExporterFailure("langfuse", ex); return; }

        foreach (var ev in events)
        {
            if (!_queue.Writer.TryWrite(ev))
            {
                ExporterDiagnostics.RecordExporterDrop("langfuse");
            }
        }
    }

    async Task DrainAsync()
    {
        var batch = new List<LangFuseEvent>(_options.MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var ev))
                {
                    batch.Add(ev);
                    if (batch.Count >= _options.MaxBatchSize) await FlushAsync(batch).ConfigureAwait(false);
                }
                if (batch.Count > 0) await FlushAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    async Task FlushAsync(List<LangFuseEvent> batch)
    {
        var snapshot = batch.ToArray();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(_options.ExportTimeout);
            await _client.SubmitAsync(snapshot, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ExporterDiagnostics.RecordExporterFailure("langfuse", ex); }
        finally { batch.Clear(); }
    }

    int _disposed;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _drain.ConfigureAwait(false); } catch { }
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _drain.GetAwaiter().GetResult(); } catch { }
        _cts.Cancel();
        _cts.Dispose();
    }
}
