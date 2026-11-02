using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SignalGrid.Ampelum.Realtime;

/// <summary>
/// <see cref="ClientWebSocket"/>-backed implementation of <see cref="IRealtimeTransport"/>.
/// Thin adapter around <c>System.Net.WebSockets</c>; covered by ContractTests rather than
/// the unit-test line-coverage gate (excluded via <c>ExcludeByFile</c> in v0.9a).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class WebSocketTransport(ClientWebSocket socket) : IRealtimeTransport
{
    readonly ClientWebSocket _socket = socket;

    /// <inheritdoc/>
    public async ValueTask ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        if (_socket.State == WebSocketState.None)
        {
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(rented, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(rented, 0, result.Count);
            } while (!result.EndOfMessage);
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc/>
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException) { /* best effort */ }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
