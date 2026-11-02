using System.Text.Json;

namespace SignalGrid.Ampelum.Agent;

/// <summary>Wraps IToolLoader + IToolDispatcher as an MCP server endpoint.
/// tools/list → SurveyAsync. tools/call → InvokeAsync through same governance.</summary>
public sealed class McpServerAdapter : IMcpServerAdapter
{
    private readonly IToolLoader _toolLoader;
    private readonly IToolDispatcher _toolDispatcher;
    private readonly McpServerOptions _options;

    public McpServerAdapter(
        IToolLoader toolLoader,
        IToolDispatcher toolDispatcher,
        McpServerOptions options)
    {
        _toolLoader = toolLoader;
        _toolDispatcher = toolDispatcher;
        _options = options;
    }

    public async Task<IReadOnlyList<ToolSurvey>> HandleToolsListAsync(CancellationToken ct = default)
    {
        return await _toolLoader.SurveyAsync(ct);
    }

    public async Task<ToolResult> HandleToolsCallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        AgentIdentity caller,
        CancellationToken ct = default)
    {
        // Convert args to JsonElement dictionary as expected by IToolDispatcher
        var jsonArgs = ConvertArgsToJsonElements(args);
        return await _toolDispatcher.InvokeAsync(toolName, jsonArgs, caller, ct);
    }

    /// <summary>Convert incoming MCP args (arbitrary objects) to JsonElement dictionary.</summary>
    private static IReadOnlyDictionary<string, JsonElement> ConvertArgsToJsonElements(
        IReadOnlyDictionary<string, object?> args)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var kvp in args)
        {
            var element = kvp.Value switch
            {
                null => JsonDocument.Parse("null").RootElement,
                string s => JsonDocument.Parse($"\"{EscapeJsonString(s)}\"").RootElement,
                bool b => JsonDocument.Parse(b ? "true" : "false").RootElement,
                int i => JsonDocument.Parse(i.ToString()).RootElement,
                long l => JsonDocument.Parse(l.ToString()).RootElement,
                double d => JsonDocument.Parse(d.ToString()).RootElement,
                decimal dec => JsonDocument.Parse(dec.ToString()).RootElement,
                _ => JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(kvp.Value)).RootElement
            };
            result[kvp.Key] = element;
        }
        return result;
    }

    private static string EscapeJsonString(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
