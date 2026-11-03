using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentService;

/// <summary>
/// Serves the order tool as an MCP server. <see cref="McpServerAdapter"/> answers <c>tools/list</c> and
/// <c>tools/call</c> in process but the library has no transport for it, so this class is the transport:
/// JSON-RPC 2.0 over one HTTP POST route, the same wire shape <see cref="HttpMcpTransport"/> speaks.
/// Every call goes through <see cref="ToolDispatcher"/>, so policy and the tool audit log apply.
/// </summary>
public sealed class OrderMcpServer
{
    public const string ToolName = "lookup_order";
    public const string Route = "/mcp";

    const string InputSchema = """{"type":"object","properties":{"orderId":{"type":"string","description":"The order id, for example A-1001."}},"required":["orderId"]}""";

    readonly McpServerAdapter _adapter;
    readonly AgentIdentity _caller = new(
        "orders-mcp", Guid.NewGuid().ToString("N"), ["orders"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
    readonly Dictionary<string, ToolManifest> _manifests;

    public OrderMcpServer()
    {
        _manifests = new Dictionary<string, ToolManifest>(StringComparer.Ordinal)
        {
            [ToolName] = new ToolManifest
            {
                Name = ToolName,
                Description = "Returns the status of an order by its id.",
                DangerTier = DangerTier.Safe,
                Reversibility = Reversibility.Reversible,
                ArgsSchema = JsonDocument.Parse(InputSchema).RootElement.Clone(),
            },
        };
        Dispatcher = new ToolDispatcher(new ManifestLoader(_manifests), new InMemoryTokenStore(), new PolicyEngine(), _caller);
        Dispatcher.RegisterHandler(ToolName, (args, _) =>
        {
            var orderId = args.TryGetValue("orderId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "";
            return Task.FromResult(new ToolResult(true, OrderData.Lookup(orderId), null, null));
        });
        _adapter = new McpServerAdapter(new ManifestLoader(_manifests), Dispatcher, new McpServerOptions("orders"));
    }

    /// <summary>The dispatcher behind <c>tools/call</c>; its audit log shows each call that reached a tool.</summary>
    public ToolDispatcher Dispatcher { get; }

    /// <summary>Adds the MCP route to a web application. The caller picks the server (Kestrel or TestServer).</summary>
    public WebApplication Map(WebApplication app)
    {
        app.MapPost(Route, async (HttpContext http, CancellationToken ct) =>
        {
            JsonElement request;
            try
            {
                request = (await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct)).RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.Content(Error(null, -32700, "Parse error").ToJsonString(), "application/json");
            }
            var response = await HandleAsync(request, ct);
            // A request without an id is a notification and gets no JSON-RPC response.
            return response is null
                ? Results.Accepted()
                : Results.Content(response.ToJsonString(), "application/json");
        });
        return app;
    }

    /// <summary>Answers one JSON-RPC envelope; null for a notification.</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("method", out var methodElement))
            return Error(null, -32600, "Invalid Request");
        if (!request.TryGetProperty("id", out var idElement))
            return null;
        var id = JsonNode.Parse(idElement.GetRawText());
        var method = methodElement.GetString();
        var parameters = request.TryGetProperty("params", out var p) ? p : default;

        switch (method)
        {
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = await ListAsync(ct) });
            case "tools/call":
                if (parameters.ValueKind != JsonValueKind.Object
                    || !parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    return Error(id, -32602, "tools/call needs a tool name");
                return Result(id, await CallAsync(name.GetString()!, parameters, ct));
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    async Task<JsonArray> ListAsync(CancellationToken ct)
    {
        var tools = new JsonArray();
        foreach (var survey in await _adapter.HandleToolsListAsync(ct))
        {
            var tool = new JsonObject
            {
                ["name"] = survey.Name,
                ["description"] = survey.Description,
                ["_meta"] = new JsonObject
                {
                    ["dangerTier"] = survey.DangerTier.ToString(),
                    ["requiresConfirmation"] = survey.RequiresConfirmation,
                },
            };
            if (_manifests.TryGetValue(survey.Name, out var manifest) && manifest.ArgsSchema is { } schema)
                tool["inputSchema"] = JsonNode.Parse(schema.GetRawText());
            tools.Add(tool);
        }
        return tools;
    }

    async Task<JsonObject> CallAsync(string toolName, JsonElement parameters, CancellationToken ct)
    {
        var arguments = new Dictionary<string, object?>();
        if (parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            foreach (var property in args.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();

        var result = await _adapter.HandleToolsCallAsync(toolName, arguments, _caller, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Success ? result.Output : result.Error }),
            ["isError"] = !result.Success,
        };
    }

    static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    sealed class ManifestLoader(IReadOnlyDictionary<string, ToolManifest> manifests) : IToolLoader
    {
        public Task<IReadOnlyList<ToolSurvey>> SurveyAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolSurvey>>([.. manifests.Values.Select(m => new ToolSurvey
            {
                Name = m.Name,
                Description = m.Description,
                DangerTier = m.DangerTier,
                RequiresConfirmation = m.RequiresConfirmation,
            })]);

        public Task<ToolManifest> UnpackAsync(string toolName, CancellationToken ct = default) =>
            Task.FromResult(manifests.TryGetValue(toolName, out var manifest)
                ? manifest
                : throw new KeyNotFoundException($"Tool '{toolName}' not found."));

        public Task<ToolManifest?> TryUnpackAsync(string toolName, CancellationToken ct = default) =>
            Task.FromResult(manifests.GetValueOrDefault(toolName));

        public ValueTask EvictAsync(string toolName) => ValueTask.CompletedTask;

        public async Task<IReadOnlyList<ToolSurvey>> SearchSurveyAsync(string query, CancellationToken ct = default) =>
            [.. (await SurveyAsync(ct)).Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }
}
