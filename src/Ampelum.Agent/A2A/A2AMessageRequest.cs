namespace SignalGrid.Ampelum.Agent.A2A;

/// <summary>A2A JSON-RPC request envelope (JSON-RPC 2.0).</summary>
public sealed record A2ARequest
{
    public string Jsonrpc { get; init; } = "2.0";
    public required string Method { get; init; }
    public required A2ARequestParams Params { get; init; }
    public string? Id { get; init; }
}

/// <summary>A2A JSON-RPC request parameters.</summary>
public sealed record A2ARequestParams
{
    public string? Id { get; init; }
    public string? ContextId { get; init; }
    public A2AMessage? Message { get; init; }
    public MessageCollection? Messages { get; init; }
    public string? HistoryLength { get; init; }
    public bool? ReturnImmediately { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>A2A JSON-RPC response envelope.</summary>
public sealed record A2AResponse
{
    public string Jsonrpc { get; init; } = "2.0";
    public A2AResult? Result { get; init; }
    public A2AError? Error { get; init; }
    public string? Id { get; init; }
}

/// <summary>Successful JSON-RPC result.</summary>
public sealed record A2AResult
{
    public A2ATask? Task { get; init; }
    public IReadOnlyList<A2ATask>? Tasks { get; init; }
    public string? NextPageToken { get; init; }
    public int? PageSize { get; init; }
    public int? TotalSize { get; init; }
}

/// <summary>JSON-RPC error object.</summary>
public sealed record A2AError
{
    public required int Code { get; init; }
    public required string Message { get; init; }
    public object? Data { get; init; }
}

/// <summary>SSE stream event from A2A streaming endpoint.</summary>
public sealed record A2AStreamEvent
{
    public required string Event { get; init; }  // "task", "error", "done"
    public required string Data { get; init; }    // JSON-encoded payload
}

/// <summary>Well-known A2A JSON-RPC method names.</summary>
public static class A2AMethods
{
    public const string SendMessage = "tasks/send";
    public const string SendStreamingMessage = "tasks/sendSubscribe";
    public const string GetTask = "tasks/get";
    public const string ListTasks = "tasks/list";
    public const string CancelTask = "tasks/cancel";
    public const string SendFeedback = "tasks/feedback";

    /// <summary>Well-known A2A agent card URL path.</summary>
    public const string AgentCardPath = "/.well-known/agent-card";
}