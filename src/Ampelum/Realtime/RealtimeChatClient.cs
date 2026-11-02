using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SignalGrid.Ampelum.Realtime;

/// <summary>
/// Minimal WebSocket-shaped transport abstraction so <see cref="RealtimeChatClient"/> can be
/// unit-tested against an in-memory fake without spinning up a real listener.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IRealtimeTransport : IAsyncDisposable
{
    /// <summary>Connects the transport to <paramref name="endpoint"/> if not already open.</summary>
    ValueTask ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);
    /// <summary>Sends a JSON text frame.</summary>
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);
    /// <summary>Receives the next JSON text frame, or <see langword="null"/> on close.</summary>
    ValueTask<string?> ReceiveTextAsync(CancellationToken cancellationToken);
    /// <summary>Closes the transport gracefully.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thin <see cref="IChatClient"/> wrapper around the OpenAI Realtime WebSocket transport.
/// v0.9a ships transport only â€” text round-trip via <c>response.create</c> +
/// <c>response.text.delta</c>/<c>response.done</c>. Audio + streaming response paths are
/// deferred to a future milestone (PSQ-8).
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
#pragma warning disable AMPELUM_V09_PREVIEW
public sealed class RealtimeChatClient : IChatClient, IAsyncDisposable, IDisposable
{
    readonly Uri _endpoint;
    readonly string _apiKey;
    readonly IRealtimeTransport _transport;
    readonly ILogger<RealtimeChatClient> _logger;
    bool _sessionInitialized;

    /// <summary>Initialises the client over a real <see cref="ClientWebSocket"/> transport.</summary>
    public RealtimeChatClient(
        Uri endpoint,
        string apiKey,
        ClientWebSocket? socket = null,
        ILogger<RealtimeChatClient>? logger = null)
        : this(endpoint, apiKey, new WebSocketTransport(socket ?? new ClientWebSocket()), logger) { }

    /// <summary>Initialises the client over an arbitrary <see cref="IRealtimeTransport"/>.</summary>
    public RealtimeChatClient(
        Uri endpoint,
        string apiKey,
        IRealtimeTransport transport,
        ILogger<RealtimeChatClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(transport);
        _endpoint = endpoint;
        _apiKey = apiKey;
        _transport = transport;
        _logger = logger ?? NullLogger<RealtimeChatClient>.Instance;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var modelId = _sessionInitialized
            ? null
            : options?.ModelId ?? throw new ArgumentException("ModelId is required.", nameof(options));

        await _transport.ConnectAsync(_endpoint, _apiKey, cancellationToken).ConfigureAwait(false);

        if (!_sessionInitialized)
        {
            var sessionUpdate = new JsonObject
            {
                ["type"] = "session.update",
                ["session"] = new JsonObject
                {
                    ["modalities"] = new JsonArray("text"),
                    ["model"] = modelId,
                },
            };
            await _transport.SendTextAsync(sessionUpdate.ToJsonString(), cancellationToken).ConfigureAwait(false);
            _sessionInitialized = true;
        }

        var msgList = messages.ToList();
        var responseCreate = new JsonObject
        {
            ["type"] = "response.create",
            ["response"] = new JsonObject
            {
                ["modalities"] = new JsonArray("text"),
                ["instructions"] = string.Concat(msgList.Select(m => m.Text ?? "")),
            },
        };
        await _transport.SendTextAsync(responseCreate.ToJsonString(), cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await _transport.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
            if (raw is null) break;
            var node = JsonNode.Parse(raw) as JsonObject;
            if (node is null) continue;
            var type = node["type"]?.GetValue<string>();
            switch (type)
            {
                case "response.text.delta":
                    var delta = node["delta"]?.GetValue<string>();
                    if (delta is not null) sb.Append(delta);
                    break;
                case "response.done":
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
                case "error":
                    var msg = node["error"]?["message"]?.GetValue<string>() ?? "Realtime error";
                    throw new InvalidOperationException(msg);
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "RealtimeChatClient streaming not implemented in v0.9a. Use GetResponseAsync.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(IRealtimeTransport)) return _transport;
        if (serviceType.IsInstanceOfType(this)) return this;
        return null;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try { await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
#pragma warning restore AMPELUM_V09_PREVIEW
