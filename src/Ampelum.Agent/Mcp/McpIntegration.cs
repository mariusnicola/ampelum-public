namespace SignalGrid.Ampelum.Agent;

/// <summary>Integrates external MCP tools into the agent's tool ecosystem.
/// tools/list → SurveyAsync. tools/call → Dispatch through PolicyEngine + AuditLog.</summary>
public interface IMcpServerAdapter
{
    /// <summary>List tools for MCP clients (maps to IToolLoader.SurveyAsync).</summary>
    Task<IReadOnlyList<ToolSurvey>> HandleToolsListAsync(CancellationToken ct = default);

    /// <summary>Call a tool from an MCP client (maps to IToolDispatcher.InvokeAsync).</summary>
    Task<ToolResult> HandleToolsCallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        AgentIdentity caller,
        CancellationToken ct = default);
}

/// <summary>Source of tools from an external MCP server.
/// External tool names are prefixed with source prefix to prevent shadow-tool attacks.</summary>
/// <remarks>
/// This is the governed contract: it carries trust/danger-tier semantics for
/// <c>ToolDispatcher</c> and is implemented by <see cref="McpToolSource"/>, which wraps an
/// <see cref="IMcpTransport"/> — the transport-agnostic seam. That seam is what makes this path
/// swappable onto a different wire implementation (e.g. one backed by the official
/// <c>ModelContextProtocol.Core</c> SDK) without changing this interface.
/// <para>
/// Deliberately distinct from <c>SignalGrid.Ampelum.Mcp.IMcpClientToolSource</c> (in
/// <c>Ampelum.Mcp/IMcpToolSource.cs</c>), which is an ungoverned <c>AIFunction</c> bridge with no
/// trust/danger-tier concept. Merging the two would require <c>Ampelum.Mcp</c> to depend on this
/// package's governance types, inverting its intentionally thin, dependency-light role.
/// </para>
/// </remarks>
public interface IMcpToolSource : IToolSource
{
    /// <summary>Trust level at which this source is connected.
    /// Tools from lower-trust sources get stricter blast radius.</summary>
    TrustLevel ConnectionTrust { get; }
}