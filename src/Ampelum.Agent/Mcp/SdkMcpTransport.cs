using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace SignalGrid.Ampelum.Agent;

/// <summary>How to start an MCP server as a child process that talks over stdio.</summary>
/// <param name="Command">Executable to start.</param>
/// <param name="Arguments">Command line arguments, one entry per argument.</param>
/// <param name="WorkingDirectory">Working directory of the child; null keeps the current one.</param>
/// <param name="Environment">Extra environment variables for the child; a null value removes the variable.</param>
public sealed record McpStdioConnection(
    string Command,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>The MCP transport on the official ModelContextProtocol.Core client, for stdio and HTTP.
/// The SDK does the handshake, session handling and paging; this class maps its tools to
/// <see cref="McpToolDefinition"/> (including the <c>_meta</c> governance annotations, read by the same
/// rules as <see cref="HttpMcpTransport"/>) and its call results to <see cref="McpToolsCallResponse"/>.
/// It carries no governance itself: <see cref="McpToolSource"/> caps the declared tiers and
/// <see cref="ToolDispatcher"/> decides whether a call is made.
/// The connection opens on first use. Disposing closes it, and for stdio that ends the child process.</summary>
public sealed class SdkMcpTransport : IMcpTransport, IAsyncDisposable
{
    private readonly IClientTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    /// <summary>Wraps any SDK client transport. Prefer <see cref="CreateStdio"/> or <see cref="CreateHttp"/>.</summary>
    public SdkMcpTransport(IClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    /// <summary>The SDK starts <paramref name="connection"/>'s command on first use and owns the child process.</summary>
    public static SdkMcpTransport CreateStdio(McpStdioConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var options = new StdioClientTransportOptions
        {
            Command = connection.Command,
            Name = Path.GetFileName(connection.Command),
            WorkingDirectory = connection.WorkingDirectory,
        };
        if (connection.Arguments is not null) options.Arguments = [.. connection.Arguments];
        if (connection.Environment is not null) options.EnvironmentVariables = new Dictionary<string, string?>(connection.Environment);
        return new SdkMcpTransport(new StdioClientTransport(options));
    }

    /// <summary>Talks to the server at <c>connection.ServerUri</c>; the optional key goes out as a bearer token.
    /// A passed <paramref name="httpClient"/> stays owned by the caller.</summary>
    public static SdkMcpTransport CreateHttp(McpServerConnection connection, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var options = new HttpClientTransportOptions { Endpoint = new Uri(connection.ServerUri) };
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + connection.ApiKey };
        }
        return new SdkMcpTransport(httpClient is null
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: false));
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            return [.. tools.Select(t => ToDefinition(t.ProtocolTool))];
        }
        catch (Exception ex) when (IsFault(ex))
        {
            throw new McpTransportException($"MCP tools/list failed: {ex.Message}", ex);
        }
    }

    public async Task<McpToolsCallResponse> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: ct).ConfigureAwait(false);
            var content = JsonSerializer.SerializeToElement(result.Content, McpJsonUtilities.DefaultOptions);
            return result.IsError == true
                ? new McpToolsCallResponse(Success: false, Result: null, Error: McpContent.Text(content) is { Length: > 0 } text ? text : content.GetRawText())
                : new McpToolsCallResponse(Success: true, Result: content);
        }
        catch (Exception ex) when (IsFault(ex))
        {
            throw new McpTransportException($"MCP tools/call '{toolName}' failed: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
            else if (_transport is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return _client ??= await McpClient.CreateAsync(_transport, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsFault(ex))
            {
                throw new McpTransportException($"MCP connection failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsFault(Exception ex) => ex is McpException or IOException or HttpRequestException;

    private static McpToolDefinition ToDefinition(ModelContextProtocol.Protocol.Tool tool)
    {
        JsonElement? meta = tool.Meta is null ? null : JsonSerializer.SerializeToElement(tool.Meta);
        var (dangerTier, requiresConfirmation) = HttpMcpTransport.ReadGovernanceMeta(meta);
        JsonElement? schema = tool.InputSchema.ValueKind == JsonValueKind.Undefined ? null : tool.InputSchema.Clone();
        return new McpToolDefinition(tool.Name, tool.Description ?? "", dangerTier, requiresConfirmation, schema);
    }
}

/// <summary>Reads the text out of a <c>tools/call</c> content array.</summary>
internal static class McpContent
{
    /// <summary>The <c>text</c> of each content item, joined; any other result as its string form.</summary>
    public static string Text(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Array } content) return result?.ToString() ?? "";
        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text.Append(t.GetString());
        }
        return text.ToString();
    }
}
