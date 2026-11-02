using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Options for the MCP server adapter.</summary>
public sealed record McpServerOptions(
    string ServerName,
    string ServerVersion = "1.0.0",
    bool EnableProgressReporting = false
);

/// <summary>Request to list available tools.</summary>
public sealed record McpToolsListRequest(string? Cursor = null);

/// <summary>Response listing available tools.</summary>
public sealed record McpToolsListResponse(
    IReadOnlyList<McpToolDefinition> Tools,
    string? NextCursor = null
);

/// <summary>A tool definition as exposed via MCP protocol.</summary>
public sealed record McpToolDefinition(
    string Name,
    string Description,
    string DangerTier,
    bool RequiresConfirmation,
    JsonElement? InputSchema = null
);

/// <summary>Request to call a specific tool.</summary>
public sealed record McpToolsCallRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments
);

/// <summary>Response from a tool call.</summary>
public sealed record McpToolsCallResponse(
    bool Success,
    object? Result,
    string? Error = null
);

/// <summary>Connection to an external MCP server. Carries transport-agnostic connection
/// data (URI, assigned trust, credentials); the transport itself is supplied separately
/// (see <see cref="IMcpTransport"/>) so tests can substitute a fake without a live server.</summary>
public sealed record McpServerConnection(
    string ServerUri,
    TrustLevel AssignedTrust,
    string? ApiKey = null
);

/// <summary>Thrown when an MCP transport call fails at the protocol level
/// (JSON-RPC error response, malformed payload, transport fault).</summary>
public sealed class McpTransportException : Exception
{
    public McpTransportException(string message) : base(message)
    {
    }

    public McpTransportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Wire-level transport for the MCP protocol's `tools/list` and `tools/call`
/// methods. Kept as a narrow, internally-owned abstraction (rather than a dependency on
/// an external MCP SDK) so <see cref="McpToolSource"/> stays fully testable with a fake
/// implementation and swappable for a real one without touching governance code.</summary>
public interface IMcpTransport
{
    /// <summary>Calls the MCP `tools/list` method and returns the server's tool definitions.</summary>
    Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>Calls the MCP `tools/call` method for a single tool invocation.
    /// This is transport-level only — it carries no governance semantics itself;
    /// callers (see <see cref="McpServerAdapter"/>) route through <c>IToolDispatcher</c> instead
    /// of calling this directly for inbound tool execution.</summary>
    Task<McpToolsCallResponse> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default);
}

/// <summary>Real MCP transport: JSON-RPC 2.0 over the "streamable HTTP" transport
/// described by the MCP specification (a single POST endpoint accepting a JSON-RPC
/// envelope). Deliberately implemented against the BCL <see cref="HttpClient"/> and
/// <see cref="System.Text.Json"/> only — this repo's central package versions do not
/// currently wire the official ModelContextProtocol.Core SDK into Ampelum.Agent, so this
/// type is the "real, working" transport within that constraint. Swapping in the official
/// SDK later is a drop-in replacement of this class behind <see cref="IMcpTransport"/>.</summary>
public sealed class HttpMcpTransport : IMcpTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Uri _endpoint;
    private int _nextRequestId;

    public HttpMcpTransport(McpServerConnection connection, HttpClient? httpClient = null)
    {
        _endpoint = new Uri(connection.ServerUri);
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        }
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await SendAsync("tools/list", payload: null, ct).ConfigureAwait(false);
        var tools = new List<McpToolDefinition>();
        if (result.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolsElement.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? throw new McpTransportException("MCP tools/list returned a tool with no name.");
                var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var (dangerTier, requiresConfirmation) = ReadGovernanceAnnotations(tool);
                JsonElement? inputSchema = tool.TryGetProperty("inputSchema", out var schema) ? schema.Clone() : null;
                tools.Add(new McpToolDefinition(name, description, dangerTier, requiresConfirmation, inputSchema));
            }
        }
        return tools;
    }

    public async Task<McpToolsCallResponse> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["name"] = toolName, ["arguments"] = arguments };
        var result = await SendAsync("tools/call", payload, ct).ConfigureAwait(false);
        var isError = result.TryGetProperty("isError", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True;
        object? content = result.TryGetProperty("content", out var contentElement) ? contentElement.Clone() : null;
        return isError
            ? new McpToolsCallResponse(Success: false, Result: null, Error: content?.ToString() ?? "Tool call failed.")
            : new McpToolsCallResponse(Success: true, Result: content);
    }

    /// <summary>MCP's base tool schema has no danger-tier field; this codebase requires one
    /// for governance, so a server-supplied `_meta.dangerTier` / `_meta.requiresConfirmation`
    /// annotation is read when present. This method does NOT pick a fail-safe tier itself:
    /// when the annotation is absent or unparseable, the empty-string sentinel is returned
    /// unchanged, which <see cref="McpToolSource"/> then fails to parse as a
    /// <see cref="DangerTier"/> and maps to its own conservative fallback. Deciding the
    /// fail-safe tier in exactly one place (rather than here AND in
    /// <see cref="McpToolSource"/> disagreeing with each other) avoids a stricter fallback
    /// silently becoming unreachable dead code behind a looser one.
    /// <c>requiresConfirmation</c> still defaults to <c>true</c> here — that default is safe
    /// standalone (it only ever demands MORE confirmation than a server-declared <c>false</c>
    /// would), unlike the danger-tier default which was silently downgrading governance.</summary>
    private static (string DangerTier, bool RequiresConfirmation) ReadGovernanceAnnotations(JsonElement tool)
    {
        var dangerTier = string.Empty;
        var requiresConfirmation = true;
        if (tool.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("dangerTier", out var tierElement) && tierElement.ValueKind == JsonValueKind.String)
            {
                var candidate = tierElement.GetString();
                if (candidate is not null && Enum.TryParse<DangerTier>(candidate, ignoreCase: true, out _))
                {
                    dangerTier = candidate;
                }
            }
            if (meta.TryGetProperty("requiresConfirmation", out var confirmElement) &&
                (confirmElement.ValueKind == JsonValueKind.True || confirmElement.ValueKind == JsonValueKind.False))
            {
                requiresConfirmation = confirmElement.GetBoolean();
            }
        }
        return (dangerTier, requiresConfirmation);
    }

    private async Task<JsonElement> SendAsync(string method, object? payload, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = payload
        };
        var body = JsonSerializer.Serialize(envelope);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errorElement))
        {
            throw new McpTransportException($"MCP server returned a JSON-RPC error for '{method}': {errorElement}");
        }
        if (!root.TryGetProperty("result", out var resultElement))
        {
            throw new McpTransportException($"MCP server response for '{method}' had no 'result' field.");
        }
        return resultElement.Clone();
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
