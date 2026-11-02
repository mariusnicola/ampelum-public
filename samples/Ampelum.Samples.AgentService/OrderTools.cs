using System.ComponentModel;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Mcp;

namespace Ampelum.Samples.AgentService;

public sealed record Order(string Id, string Status, string Carrier, string Eta);

/// <summary>The order data behind the tools.</summary>
public static class OrderData
{
    public static IReadOnlyDictionary<string, Order> Orders { get; } = new Dictionary<string, Order>(StringComparer.Ordinal)
    {
        ["A-1001"] = new("A-1001", "Shipped", "NorthPost", "2 business days"),
        ["A-1002"] = new("A-1002", "Processing", "none yet", "unknown"),
    };

    public static string Lookup(string orderId) =>
        Orders.TryGetValue(orderId.Trim(), out var order)
            ? $"Order {order.Id}: {order.Status}, carrier {order.Carrier}, delivery in {order.Eta}."
            : $"No order with id {orderId}.";
}

/// <summary>
/// Stands in for a connected MCP session by listing tools as <see cref="AIFunction"/>. No MCP server
/// runs and no transport is involved; this is the seam <see cref="McpToolFactory"/> offers for a source
/// that is not a live <c>McpClient</c>.
/// </summary>
public sealed class OrderToolSource : IMcpClientToolSource
{
    public Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct)
    {
        IReadOnlyList<AIFunction> tools =
        [
            AIFunctionFactory.Create(
                ([Description("The order id, for example A-1001.")] string orderId) => OrderData.Lookup(orderId),
                "lookup_order",
                "Returns the status of an order by its id."),
        ];
        return Task.FromResult(tools);
    }
}

/// <summary>
/// The wire side of an MCP server, answering <c>tools/list</c> with a tool that declares itself safe.
/// The governed tool source ignores that claim, which is what the survey in this sample shows.
/// </summary>
public sealed class OrderToolTransport : IMcpTransport
{
    public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<McpToolDefinition> tools =
        [
            new McpToolDefinition("lookup_order", "Returns the status of an order by its id.", nameof(DangerTier.Safe), RequiresConfirmation: false),
        ];
        return Task.FromResult(tools);
    }

    public Task<McpToolsCallResponse> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default) =>
        Task.FromResult(new McpToolsCallResponse(Success: true, Result: OrderData.Lookup(arguments["orderId"]?.ToString() ?? "")));
}
