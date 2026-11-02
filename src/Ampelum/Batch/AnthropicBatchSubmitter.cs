using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Batch;

/// <summary>
/// File-based async batch submitter for the Anthropic Messages Batch API (PSQ-4, v0.9g).
/// Uploads JSONL inline to <c>POST /v1/messages/batches</c>, polls
/// <c>GET /v1/messages/batches/{id}</c> and streams results from
/// <c>GET /v1/messages/batches/{id}/results</c>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class AnthropicBatchSubmitter : IBatchChatClient
{
    /// <summary>Symbolic provider name written to audit metadata.</summary>
    public const string ProviderName = "anthropic";

    /// <summary>Anthropic API version pinned by this submitter.</summary>
    public const string ApiVersion = "2023-06-01";

    static readonly Uri DefaultBaseAddress = new("https://api.anthropic.com/");

    readonly HttpClient _http;
    readonly TimeProvider _timeProvider;

    /// <summary>Creates a new submitter using the supplied <paramref name="httpClient"/>.</summary>
    /// <param name="httpClient">Pre-configured client.</param>
    /// <param name="apiKey">Optional Anthropic API key (sent as <c>x-api-key</c>).</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public AnthropicBatchSubmitter(HttpClient httpClient, string? apiKey = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_http.BaseAddress is null) _http.BaseAddress = DefaultBaseAddress;
        if (!_http.DefaultRequestHeaders.Contains("anthropic-version"))
            _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        if (apiKey is { Length: > 0 } && !_http.DefaultRequestHeaders.Contains("x-api-key"))
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
    }

    /// <inheritdoc />
    public async Task<BatchHandle> SubmitAsync(
        IReadOnlyList<BatchRequest> requests,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("At least one request is required.", nameof(requests));

        var requestArray = new JsonArray();
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            ArgumentNullException.ThrowIfNull(req);
            requestArray.Add(new JsonObject
            {
                ["custom_id"] = req.CustomId,
                ["params"] = BuildMessageParams(req.Messages, WithBatchModel(req.Options, options)),
            });
        }
        var body = new JsonObject { ["requests"] = requestArray };

        using var resp = await _http.PostAsJsonAsync("v1/messages/batches", body, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Anthropic batches create returned empty body.");
        var id = json["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Anthropic batches create response missing 'id'.");
        return new BatchHandle(id, ProviderName, _timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task<BatchStatus> GetStatusAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var resp = await _http.GetAsync($"v1/messages/batches/{handle.Id}", cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Anthropic batches get returned empty body.");
        var processingStatus = json["processing_status"]?.GetValue<string>() ?? "in_progress";
        var counts = json["request_counts"];
        var succeeded = counts?["succeeded"]?.GetValue<int>() ?? 0;
        var errored = counts?["errored"]?.GetValue<int>() ?? 0;
        var canceled = counts?["canceled"]?.GetValue<int>() ?? 0;
        var expired = counts?["expired"]?.GetValue<int>() ?? 0;
        var processing = counts?["processing"]?.GetValue<int>() ?? 0;
        var completed = succeeded + errored + canceled + expired;
        var total = completed + processing;
        return new BatchStatus(MapState(processingStatus), completed, errored, total);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BatchResultItem> StreamResultsAsync(
        BatchHandle handle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var resp = await _http.GetAsync($"v1/messages/batches/{handle.Id}/results", cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return ParseResultLine(line);
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var resp = await _http.PostAsync($"v1/messages/batches/{handle.Id}/cancel", content: null, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    static JsonObject BuildMessageParams(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        // Anthropic separates system from messages.
        string? system = null;
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                system = system is null ? m.Text : system + "\n" + m.Text;
                continue;
            }
            msgs.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = m.Text,
            });
        }
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? throw new ArgumentException("ModelId is required.", nameof(options)),
            ["max_tokens"] = options?.MaxOutputTokens ?? 1024,
            ["messages"] = msgs,
        };
        if (system is not null) body["system"] = system;
        if (options?.Temperature is float temp) body["temperature"] = temp;
        return body;
    }

    static BatchState MapState(string raw) => raw switch
    {
        "ended" => BatchState.Completed,
        "canceling" or "canceled" => BatchState.Cancelled,
        _ => BatchState.InProgress,
    };

    static BatchResultItem ParseResultLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var customId = root.TryGetProperty("custom_id", out var cidElt) ? cidElt.GetString() ?? "" : "";
        if (!root.TryGetProperty("result", out var resElt))
            return new BatchResultItem(customId, Response: null, ErrorMessage: "no result");
        var type = resElt.TryGetProperty("type", out var typeElt) ? typeElt.GetString() : null;
        if (type != "succeeded")
        {
            var err = resElt.TryGetProperty("error", out var errElt) && errElt.TryGetProperty("message", out var m)
                ? m.GetString()
                : type ?? "unknown";
            return new BatchResultItem(customId, Response: null, ErrorMessage: err ?? "unknown");
        }
        if (!resElt.TryGetProperty("message", out var msgElt) || !msgElt.TryGetProperty("content", out var contentElt))
            return new BatchResultItem(customId, Response: null, ErrorMessage: "no content");
        var sb = new StringBuilder();
        foreach (var block in contentElt.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                && block.TryGetProperty("text", out var txt))
            {
                sb.Append(txt.GetString());
            }
        }
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, sb.ToString()));
        return new BatchResultItem(customId, resp, ErrorMessage: null);
    }

    // A per-request Options object must not hide the model set for the whole batch.
    static ChatOptions? WithBatchModel(ChatOptions? request, ChatOptions? batch)
    {
        if (request is null)
            return batch;
        if (request.ModelId is not null)
            return request;
        var merged = request.Clone();
        merged.ModelId = batch?.ModelId;
        return merged;
    }
}
