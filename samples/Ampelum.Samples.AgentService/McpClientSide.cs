using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Mcp;

namespace Ampelum.Samples.AgentService;

/// <summary>
/// Offers the tools of a server behind an <see cref="IMcpTransport"/> as <see cref="AIFunction"/>, so
/// <see cref="ToolAgent"/> can use them. Listing is <c>tools/list</c>, invoking a function is <c>tools/call</c>.
/// </summary>
public sealed class TransportToolSource(IMcpTransport transport) : IMcpClientToolSource
{
    public async Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct)
    {
        var definitions = await transport.ListToolsAsync(ct);
        return [.. definitions.Select(d => new TransportFunction(transport, d))];
    }

    /// <summary>The text of a <c>tools/call</c> result: the <c>text</c> of each content item, joined.</summary>
    public static string Text(object? result)
    {
        if (result is not JsonElement { ValueKind: JsonValueKind.Array } content)
            return result?.ToString() ?? "";
        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
            if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        return text.ToString();
    }

    sealed class TransportFunction(IMcpTransport transport, McpToolDefinition definition) : AIFunction
    {
        static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public override string Name => definition.Name;
        public override string Description => definition.Description;
        public override JsonElement JsonSchema => definition.InputSchema ?? EmptySchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken ct)
        {
            var call = await transport.CallToolAsync(
                definition.Name, arguments.ToDictionary(a => a.Key, a => a.Value), ct);
            return call.Success ? Text(call.Result) : $"Tool error: {call.Error}";
        }
    }
}
