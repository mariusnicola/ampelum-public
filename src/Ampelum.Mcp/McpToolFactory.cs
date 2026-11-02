using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace SignalGrid.Ampelum.Mcp;

/// <summary>
/// Bridges Model Context Protocol (MCP) tools to <see cref="AIFunction"/> instances that can be
/// used with any <see cref="IChatClient"/> and Ampelum pipelines.
/// </summary>
/// <remarks>
/// <para>
/// In <c>ModelContextProtocol.Core</c> 1.2.0, <see cref="McpClientTool"/> already extends
/// <see cref="AIFunction"/> — including its own JSON schema construction and the routing of
/// <see cref="AIFunction.InvokeAsync"/> to the underlying <c>CallToolAsync</c>. This factory
/// therefore does NOT re-wrap tools via <c>AIFunctionFactory.Create</c>; doing so would strip the
/// native schema and dispatch logic. Instead it calls
/// <see cref="McpClient.ListToolsAsync(ModelContextProtocol.RequestOptions?,CancellationToken)"/>
/// and projects the resulting <see cref="McpClientTool"/> list directly to
/// <see cref="AIFunction"/>.
/// </para>
/// <para>
/// For unit-testability (because <see cref="McpClient"/> is abstract and requires a live transport
/// to do real work), the factory also exposes a public overload that accepts an
/// <see cref="IMcpClientToolSource"/> seam. Tests supply a fake implementation of that interface
/// without needing a real MCP server; other callers may supply their own implementation instead of
/// depending on the concrete <see cref="McpClient"/> type.
/// </para>
/// </remarks>
public static class McpToolFactory
{
    /// <summary>
    /// Retrieves all tools exposed by an MCP session and returns them as
    /// <see cref="AIFunction"/> instances ready for use with any <see cref="IChatClient"/>.
    /// </summary>
    /// <param name="session">The connected MCP client session to query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>
    /// A read-only list of <see cref="AIFunction"/> objects — one per tool advertised by the
    /// server. Returns an empty list when the server exposes no tools; never throws for an
    /// empty tool set.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="session"/> is <see langword="null"/>.
    /// </exception>
    public static Task<IReadOnlyList<AIFunction>> FromMcpSessionAsync(
        McpClient session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return FromMcpSessionAsync(new McpClientAdapter(session), ct);
    }

    /// <summary>
    /// Retrieves all tools from an arbitrary <see cref="IMcpClientToolSource"/> seam and returns
    /// them as <see cref="AIFunction"/> instances. Public so callers can depend on the abstraction
    /// (e.g. a fake for tests, or a future non-<see cref="McpClient"/> source) instead of only the
    /// concrete <see cref="McpClient"/> overload above.
    /// </summary>
    /// <param name="source">The tool source to query.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static async Task<IReadOnlyList<AIFunction>> FromMcpSessionAsync(
        IMcpClientToolSource source,
        CancellationToken ct = default)
    {
        return await source.ListToolsAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Private adapter — wraps McpClient into IMcpClientToolSource
    // -------------------------------------------------------------------------

    private sealed class McpClientAdapter(McpClient client) : IMcpClientToolSource
    {
        public async Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct)
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            // McpClientTool derives from AIFunction — no information is lost in this cast.
            return tools.Cast<AIFunction>().ToList().AsReadOnly();
        }
    }
}
