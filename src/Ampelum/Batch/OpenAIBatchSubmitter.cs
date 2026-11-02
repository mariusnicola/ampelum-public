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
/// File-based async batch submitter for the OpenAI Batch API (PSQ-4, v0.9g).
/// Uploads JSONL via <c>POST /v1/files</c>, creates the batch via <c>POST /v1/batches</c>,
/// polls <c>GET /v1/batches/{id}</c> and downloads results via <c>GET /v1/files/{output_file_id}/content</c>.
/// </summary>
/// <remarks>
/// Uses the raw HTTP surface so that mocked <see cref="HttpMessageHandler"/> tests cover
/// every protocol branch without requiring the OpenAI SDK's typed BatchClient. The default
/// base address is <c>https://api.openai.com/v1/</c>; override for OpenRouter / Azure.
/// </remarks>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class OpenAIBatchSubmitter : IBatchChatClient
{
    /// <summary>Symbolic provider name written to audit metadata.</summary>
    public const string ProviderName = "openai";

    static readonly Uri DefaultBaseAddress = new("https://api.openai.com/v1/");

    readonly HttpClient _http;
    readonly TimeProvider _timeProvider;

    /// <summary>Creates a new submitter using the supplied <paramref name="httpClient"/>.</summary>
    /// <param name="httpClient">Pre-configured client. Auth header (<c>Authorization: Bearer ...</c>) is set if not already present and <paramref name="apiKey"/> is provided.</param>
    /// <param name="apiKey">Optional OpenAI API key; if null, caller must add auth on <paramref name="httpClient"/>.</param>
    /// <param name="timeProvider">Clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public OpenAIBatchSubmitter(HttpClient httpClient, string? apiKey = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_http.BaseAddress is null) _http.BaseAddress = DefaultBaseAddress;
        if (apiKey is { Length: > 0 } && _http.DefaultRequestHeaders.Authorization is null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <inheritdoc />
    public async Task<BatchHandle> SubmitAsync(
        IReadOnlyList<BatchRequest> requests,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("At least one request is required.", nameof(requests));

        // 1. Build JSONL payload.
        var jsonl = BuildJsonl(requests, options);

        // 2. Upload file as multipart/form-data with purpose=batch.
        using var fileContent = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes(jsonl);
        var fileBytes = new ByteArrayContent(bytes);
        fileBytes.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        fileContent.Add(new StringContent("batch"), "purpose");
        fileContent.Add(fileBytes, "file", "batch.jsonl");

        using var uploadResp = await _http.PostAsync("files", fileContent, cancellationToken).ConfigureAwait(false);
        uploadResp.EnsureSuccessStatusCode();
        var uploadJson = await uploadResp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI files upload returned empty body.");
        var inputFileId = uploadJson["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("OpenAI files upload response missing 'id'.");

        // 3. Create the batch.
        var createBody = new JsonObject
        {
            ["input_file_id"] = inputFileId,
            ["endpoint"] = "/v1/chat/completions",
            ["completion_window"] = "24h",
        };
        using var createResp = await _http.PostAsJsonAsync("batches", createBody, cancellationToken).ConfigureAwait(false);
        createResp.EnsureSuccessStatusCode();
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI batches create returned empty body.");
        var batchId = createJson["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("OpenAI batches create response missing 'id'.");

        return new BatchHandle(batchId, ProviderName, _timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task<BatchStatus> GetStatusAsync(BatchHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        using var resp = await _http.GetAsync($"batches/{handle.Id}", cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI batches get returned empty body.");
        var statusStr = json["status"]?.GetValue<string>() ?? "in_progress";
        var counts = json["request_counts"];
        var completed = counts?["completed"]?.GetValue<int>() ?? 0;
        var failed = counts?["failed"]?.GetValue<int>() ?? 0;
        var total = counts?["total"]?.GetValue<int>() ?? 0;
        return new BatchStatus(MapState(statusStr), completed, failed, total);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BatchResultItem> StreamResultsAsync(
        BatchHandle handle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        // Fetch the batch metadata to find the output_file_id.
        using var batchResp = await _http.GetAsync($"batches/{handle.Id}", cancellationToken).ConfigureAwait(false);
        batchResp.EnsureSuccessStatusCode();
        var batchJson = await batchResp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI batches get returned empty body.");
        var outputFileId = batchJson["output_file_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("OpenAI batch has no output_file_id; batch is not complete.");

        using var contentResp = await _http.GetAsync($"files/{outputFileId}/content", cancellationToken).ConfigureAwait(false);
        contentResp.EnsureSuccessStatusCode();
        await using var stream = await contentResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
        using var resp = await _http.PostAsync($"batches/{handle.Id}/cancel", content: null, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    static string BuildJsonl(IReadOnlyList<BatchRequest> requests, ChatOptions? defaultOptions)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < requests.Count; i++)
        {
            var req = requests[i];
            ArgumentNullException.ThrowIfNull(req);
            var body = new JsonObject
            {
                ["custom_id"] = req.CustomId,
                ["method"] = "POST",
                ["url"] = "/v1/chat/completions",
                ["body"] = BuildChatBody(req.Messages, WithBatchModel(req.Options, defaultOptions)),
            };
            sb.Append(body.ToJsonString());
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static JsonObject BuildChatBody(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            msgs.Add(new JsonObject
            {
                ["role"] = m.Role.Value,
                ["content"] = m.Text,
            });
        }
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? throw new ArgumentException("ModelId is required.", nameof(options)),
            ["messages"] = msgs,
        };
        if (options?.MaxOutputTokens is int max) body["max_tokens"] = max;
        if (options?.Temperature is float temp) body["temperature"] = temp;
        return body;
    }

    static BatchState MapState(string raw) => raw switch
    {
        "completed" => BatchState.Completed,
        "failed" => BatchState.Failed,
        "cancelled" or "cancelling" => BatchState.Cancelled,
        "expired" => BatchState.Expired,
        _ => BatchState.InProgress,
    };

    static BatchResultItem ParseResultLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var customId = root.TryGetProperty("custom_id", out var cidElt) ? cidElt.GetString() ?? "" : "";
        if (root.TryGetProperty("error", out var errElt) && errElt.ValueKind != JsonValueKind.Null)
        {
            string? msg;
            if (errElt.ValueKind == JsonValueKind.Object && errElt.TryGetProperty("message", out var m))
                msg = m.GetString();
            else
                msg = errElt.GetRawText();
            return new BatchResultItem(customId, Response: null, ErrorMessage: msg ?? "unknown");
        }
        if (!root.TryGetProperty("response", out var respElt))
            return new BatchResultItem(customId, Response: null, ErrorMessage: "no response");
        if (!respElt.TryGetProperty("body", out var bodyElt))
            return new BatchResultItem(customId, Response: null, ErrorMessage: "no body");
        if (!bodyElt.TryGetProperty("choices", out var choicesElt) || choicesElt.GetArrayLength() == 0)
            return new BatchResultItem(customId, Response: null, ErrorMessage: "no choices");
        var first = choicesElt[0];
        var content = first.TryGetProperty("message", out var msgElt) && msgElt.TryGetProperty("content", out var contentElt)
            ? contentElt.GetString() ?? ""
            : "";
        var resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
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
