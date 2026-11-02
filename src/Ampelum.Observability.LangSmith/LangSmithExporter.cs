using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using SignalGrid.Ampelum.Observability;
using Microsoft.Extensions.Options;

namespace SignalGrid.Ampelum.Observability.LangSmith;

/// <summary>
/// <see cref="ITraceSink"/> decorator that mirrors every <see cref="TraceRecord"/>
/// onto the LangSmith ingestion API. Forwards to the wrapped inner sink first,
/// then enqueues a mapped <see cref="LangSmithRun"/> for asynchronous batched export.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class LangSmithExporter : ITraceSink, IAsyncDisposable, IDisposable
{
    readonly ITraceSink _inner;
    readonly ILangSmithClient _client;
    readonly LangSmithRunMapper _mapper;
    readonly LangSmithExporterOptions _options;
    readonly LangSmithCredentials _credentials;
    readonly Channel<LangSmithRun> _queue;
    readonly CancellationTokenSource _cts = new();
    readonly Task _drain;

    /// <summary>Creates a new exporter wrapping <paramref name="inner"/>.</summary>
    public LangSmithExporter(
        ITraceSink inner,
        ILangSmithClient client,
        LangSmithRunMapper mapper,
        IOptions<LangSmithExporterOptions> options,
        IOptions<LangSmithCredentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        _inner = inner;
        _client = client;
        _mapper = mapper;
        _options = options.Value;
        _credentials = credentials.Value;
        _queue = Channel.CreateBounded<LangSmithRun>(new BoundedChannelOptions(_options.QueueCapacity)
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
        catch { /* must-not-throw rule for trace path */ }

        LangSmithRun run;
        try
        {
            run = _mapper.Map(record, _credentials.ProjectName, _options.IncludeAuditPayload);
        }
        catch (Exception ex)
        {
            ExporterDiagnostics.RecordExporterFailure("langsmith", ex);
            return;
        }
        if (!_queue.Writer.TryWrite(run))
        {
            ExporterDiagnostics.RecordExporterDrop("langsmith");
        }
    }

    async Task DrainAsync()
    {
        var batch = new List<LangSmithRun>(_options.MaxBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var run))
                {
                    batch.Add(run);
                    if (batch.Count >= _options.MaxBatchSize)
                    {
                        await FlushAsync(batch).ConfigureAwait(false);
                    }
                }
                if (batch.Count > 0)
                {
                    await FlushAsync(batch).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    async Task FlushAsync(List<LangSmithRun> batch)
    {
        // Snapshot the batch so the caller can safely Clear() it after we return.
        var snapshot = batch.ToArray();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(_options.ExportTimeout);
            await _client.SubmitAsync(snapshot, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown / per-flush timeout */ }
        catch (Exception ex)
        {
            ExporterDiagnostics.RecordExporterFailure("langsmith", ex);
        }
        finally
        {
            batch.Clear();
        }
    }

    int _disposed;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _drain.ConfigureAwait(false); }
        catch { /* drain swallows */ }
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _drain.GetAwaiter().GetResult(); }
        catch { /* drain swallows */ }
        _cts.Cancel();
        _cts.Dispose();
    }
}
