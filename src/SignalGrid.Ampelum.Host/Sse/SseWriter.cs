using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace SignalGrid.Ampelum.Host.Sse;

internal sealed class SseWriter : IAsyncDisposable
{
    readonly HttpResponse _response;
    readonly ILogger _log;
    readonly TimeSpan _keepAlive;
    readonly Timer _timer;
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _started;
    bool _disposed;

    public SseWriter(HttpResponse response, ILogger log, TimeSpan keepAlive)
    {
        _response = response;
        _log = log;
        _keepAlive = keepAlive;
        _timer = new Timer(_ => _ = TryEmitKeepAliveAsync(), null, keepAlive, keepAlive);
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        _response.ContentType = "text/event-stream";
        _response.Headers.CacheControl = "no-cache, no-transform";
        _response.Headers.Connection = "keep-alive";
        _response.Headers["X-Accel-Buffering"] = "no";
        _response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        _started = true;
        // Write initial keep-alive comment so client knows stream is live
        await _response.Body.WriteAsync(": \n"u8.ToArray(), ct);
        await _response.Body.FlushAsync(ct);
    }

    public async ValueTask WriteEventAsync(string eventName, string jsonPayload, long? id, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct);
        try
        {
            var sb = new StringBuilder();
            sb.Append("event: ").Append(eventName).Append('\n');
            // Multi-line payload: split on \n and emit one data: per line
            var lines = jsonPayload.Split('\n');
            foreach (var line in lines)
                sb.Append("data: ").Append(line).Append('\n');
            if (id.HasValue)
                sb.Append("id: ").Append(id.Value).Append('\n');
            sb.Append('\n');
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await _response.Body.WriteAsync(bytes, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    async ValueTask TryEmitKeepAliveAsync()
    {
        if (_disposed || !_started) return;
        try
        {
            await _gate.WaitAsync();
            try
            {
                await _response.Body.WriteAsync(": \n"u8.ToArray());
                await _response.Body.FlushAsync();
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "SSE keep-alive write failed (client likely disconnected).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _timer.DisposeAsync();
    }
}
