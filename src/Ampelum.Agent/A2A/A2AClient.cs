using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalGrid.Ampelum.Agent.A2A;

/// <summary>JSON converter for MessageCollection — bridges STJ's need for mutable
/// collection construction with MessageCollection's immutable facade.</summary>
public sealed class MessageCollectionConverter : JsonConverter<MessageCollection>
{
    public override MessageCollection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = JsonSerializer.Deserialize<List<A2AMessage>>(ref reader, options);
        return list is null ? null : new MessageCollection(list);
    }

    public override void Write(Utf8JsonWriter writer, MessageCollection value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}

/// <summary>
/// Reads an enum from its name (any case, with or without "-" or "_", so "input-required" is InputRequired) or from
/// its number, and writes the number as before. A name that is not one of the enum's own fails.
/// </summary>
internal sealed class NameOrNumberEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            return (T)Enum.ToObject(typeof(T), number);

        if (reader.TokenType == JsonTokenType.String)
        {
            // Only the enum's own names count: Enum.TryParse would also take "1", "-1" and "A,B" lists.
            var text = reader.GetString()!.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
            foreach (var name in Enum.GetNames<T>())
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse<T>(name);
            }
        }

        throw new JsonException($"'{reader.GetString() ?? reader.TokenType.ToString()}' is not a {typeof(T).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Convert.ToInt32(value));
}

/// <summary>JSON-RPC HTTP client for the A2A protocol (v1.0).</summary>
public sealed class A2AClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly JsonSerializerOptions _json;

    /// <param name="http">
    /// The HTTP client to use. A client passed in belongs to the caller and is not disposed by <see cref="Dispose"/>.
    /// </param>
    public A2AClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters = { new MessageCollectionConverter(), new NameOrNumberEnumConverter<TaskState>(), new NameOrNumberEnumConverter<MessageRole>() },
        };
    }

    /// <summary>Fetch an A2A agent card from the well-known URL.</summary>
    public async Task<A2AAgentCard?> FetchAgentCardAsync(
        string baseUrl,
        CancellationToken ct = default)
    {
        var cardUrl = baseUrl.TrimEnd('/') + A2AMethods.AgentCardPath;
        var response = await _http.GetAsync(cardUrl, ct);
        response.EnsureSuccessStatusCode();

        // The HttpClient follows redirects, so the card may come from another origin than the one asked. The final
        // request URI is where it was served.
        var servedBy = response.RequestMessage?.RequestUri?.ToString() ?? cardUrl;
        if (!SameOrigin(baseUrl, servedBy))
            throw new InvalidOperationException($"The agent card was asked for at {cardUrl} but served by {servedBy}, which is not the same scheme, host and port.");

        var card = await response.Content.ReadFromJsonAsync<A2AAgentCard>(_json, ct);

        // The card says where tasks go. A card that names another origin than the one it came from would take every task there.
        if (card is not null && !SameOrigin(baseUrl, card.Url))
            throw new InvalidOperationException($"The agent card from {cardUrl} names {card.Url}, which is not the same scheme, host and port.");
        return card;
    }

    private static bool SameOrigin(string baseUrl, string cardUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var a)
        && Uri.TryCreate(cardUrl, UriKind.Absolute, out var b)
        && a.Scheme == b.Scheme
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>Send a message to an A2A agent and get the task result.</summary>
    public async Task<A2AResponse> SendMessageAsync(
        string agentUrl,
        A2AMessage message,
        string? contextId = null,
        bool returnImmediately = false,
        CancellationToken ct = default)
    {
        var request = new A2ARequest
        {
            Method = A2AMethods.SendMessage,
            Params = new A2ARequestParams
            {
                Message = message,
                ContextId = contextId,
                ReturnImmediately = returnImmediately,
            },
            Id = Guid.NewGuid().ToString("N"),
        };

        return await PostAsync(agentUrl, request, ct);
    }

    /// <summary>Send a message and subscribe to streaming updates.</summary>
    public async IAsyncEnumerable<A2AStreamEvent> SendStreamingMessageAsync(
        string agentUrl,
        A2AMessage message,
        string? contextId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new A2ARequest
        {
            Method = A2AMethods.SendStreamingMessage,
            Params = new A2ARequestParams
            {
                Message = message,
                ContextId = contextId,
            },
            Id = Guid.NewGuid().ToString("N"),
        };

        var json = JsonSerializer.Serialize(request, _json);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(agentUrl, content, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null && !ct.IsCancellationRequested)
        {
            if (line.StartsWith("event: "))
            {
                var eventType = line[7..].Trim();
                var dataLine = await reader.ReadLineAsync(ct);
                var data = dataLine?.StartsWith("data: ") == true
                    ? dataLine[6..].Trim()
                    : string.Empty;

                yield return new A2AStreamEvent
                {
                    Event = eventType,
                    Data = data,
                };
            }
        }
    }

    /// <summary>Poll the current state of a task.</summary>
    public async Task<A2AResponse> GetTaskAsync(
        string agentUrl,
        string taskId,
        string? historyLength = null,
        CancellationToken ct = default)
    {
        var request = new A2ARequest
        {
            Method = A2AMethods.GetTask,
            Params = new A2ARequestParams
            {
                Id = taskId,
                HistoryLength = historyLength,
            },
            Id = Guid.NewGuid().ToString("N"),
        };

        return await PostAsync(agentUrl, request, ct);
    }

    /// <summary>Cancel a running task.</summary>
    public async Task<A2AResponse> CancelTaskAsync(
        string agentUrl,
        string taskId,
        CancellationToken ct = default)
    {
        var request = new A2ARequest
        {
            Method = A2AMethods.CancelTask,
            Params = new A2ARequestParams
            {
                Id = taskId,
            },
            Id = Guid.NewGuid().ToString("N"),
        };

        return await PostAsync(agentUrl, request, ct);
    }

    /// <summary>List tasks with optional filtering.</summary>
    public async Task<A2AResponse> ListTasksAsync(
        string agentUrl,
        string? contextId = null,
        string? status = null,
        int? pageSize = null,
        string? pageToken = null,
        CancellationToken ct = default)
    {
        var request = new A2ARequest
        {
            Method = A2AMethods.ListTasks,
            Params = new A2ARequestParams
            {
                ContextId = contextId,
                Id = pageToken,
                HistoryLength = status,
                // PageSize and other params passed via metadata
                Metadata = pageSize.HasValue
                    ? new Dictionary<string, object?> { ["pageSize"] = pageSize.Value }
                    : null,
            },
            Id = Guid.NewGuid().ToString("N"),
        };

        return await PostAsync(agentUrl, request, ct);
    }

    private async Task<A2AResponse> PostAsync(string url, A2ARequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(url, request, _json, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<A2AResponse>(_json, ct);
        return result ?? throw new InvalidOperationException("A2A response was null");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}