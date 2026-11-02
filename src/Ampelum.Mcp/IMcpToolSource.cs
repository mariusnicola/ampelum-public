using Microsoft.Extensions.AI;

namespace SignalGrid.Ampelum.Mcp;

/// <summary>
/// Seam used by <see cref="McpToolFactory"/> to abstract over
/// <see cref="ModelContextProtocol.Client.McpClient"/> — both for unit testing and so callers who
/// only need an <see cref="AIFunction"/> bridge (no governance) can depend on this abstraction
/// instead of the concrete <see cref="ModelContextProtocol.Client.McpClient"/> type.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately different, narrower contract than
/// <c>SignalGrid.Ampelum.Agent.IMcpToolSource</c> (in <c>Ampelum.Agent/Mcp/McpIntegration.cs</c>),
/// which additionally carries governance metadata (danger tiers, trust levels, manifest
/// verification) for <c>ToolDispatcher</c>. The two are not merged: <c>Ampelum.Mcp</c> has no
/// dependency on <c>Ampelum.Agent</c>'s governance types, and inverting that dependency to force a
/// shared interface would turn this 96-line bridge package into a consumer of the governed
/// runtime. The two paths unify at the transport seam (<c>IMcpTransport</c> on the Agent side)
/// rather than here.
/// </para>
/// <para>
/// Public (promoted from <see langword="internal"/>) so that a caller who wants MCP tools as
/// <see cref="AIFunction"/> without the governance stack can supply their own
/// <see cref="IMcpClientToolSource"/> and call the public
/// <see cref="McpToolFactory.FromMcpSessionAsync(IMcpClientToolSource,CancellationToken)"/>
/// overload, rather than being limited to the concrete
/// <see cref="ModelContextProtocol.Client.McpClient"/> type.
/// </para>
/// </remarks>
public interface IMcpClientToolSource
{
    /// <summary>Returns all tools exposed by the backing MCP session.</summary>
    Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct);
}
