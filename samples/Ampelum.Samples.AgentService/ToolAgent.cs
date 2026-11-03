using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Mcp;

namespace Ampelum.Samples.AgentService;

/// <summary>A ReAct agent whose tools are loaded from an MCP tool source.</summary>
public sealed class ToolAgent : IDisposable
{
    readonly IMcpClientToolSource _tools;
    readonly ServiceProvider _provider;

    public ToolAgent(IChatClient chat, IMcpClientToolSource tools)
    {
        _tools = tools;
        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAmpelum();
        _provider = services.BuildServiceProvider();
    }

    /// <summary>Names of the tools the source offers.</summary>
    public async Task<IReadOnlyList<string>> ToolNamesAsync(CancellationToken ct = default) =>
        (await McpToolFactory.FromMcpSessionAsync(_tools, ct)).Select(t => t.Name).ToArray();

    public async Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var functions = await McpToolFactory.FromMcpSessionAsync(_tools, ct);
        var react = new ReAct<OrderStatusSignature>(ctx, functions);
        var result = await react.ForwardAsync(new OrderStatusSignature { Question = question }, ct: ct);
        return result.Value.Answer;
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>
    /// Lists tools through the governed source. Without a manifest verifier it caps every discovered tool
    /// to the most dangerous tier, whatever the server declares.
    /// </summary>
    /// <param name="transport">The wire side to list tools from.</param>
    /// <param name="serverUri">The address the tools are named after; the transport is what is called, not this address.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<ToolSurvey>> SurveyAsync(IMcpTransport transport, string serverUri, CancellationToken ct = default)
    {
        var source = new McpToolSource(new McpServerConnection(serverUri, TrustLevel.Unknown), transport);
        return await source.SurveyAsync(ct);
    }
}
