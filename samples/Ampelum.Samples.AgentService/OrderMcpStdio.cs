using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SignalGrid.Ampelum.Agent;
using SdkServerOptions = ModelContextProtocol.Server.McpServerOptions;

namespace Ampelum.Samples.AgentService;

/// <summary>
/// The order tools as an MCP server on stdio. The sample starts its own program with <see cref="Flag"/>, so the
/// server is a child process that the SDK transport starts and stops. It serves three tools: one declares itself
/// safe, one catastrophic, and one declares nothing. Every <c>tools/call</c> it gets is appended to a log file
/// before the tool runs, so a test can tell a blocked call from one that was made.
/// </summary>
public static class OrderMcpStdio
{
    public const string Flag = "--mcp-stdio-server";
    public const string CallLogVariable = "ORDER_MCP_CALL_LOG";
    public const string PidFileVariable = "ORDER_MCP_PID_FILE";
    public const string SafeTool = OrderMcpServer.ToolName;
    public const string CatastrophicTool = "purge_orders";
    public const string UnannotatedTool = "note_order";

    /// <summary>Names the stdio server as a source; it is a label, nothing is contacted at this address.</summary>
    public const string ServerUri = "stdio://orders";

    static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"orderId":{"type":"string"}}}""").RootElement.Clone();

    /// <summary>How to start the server: this program's assembly again under the dotnet host, with <see cref="Flag"/>.
    /// The host is the one running now when it is dotnet, otherwise the first <c>dotnet</c> on the path, so no
    /// app host file or execute bit is needed.</summary>
    public static McpStdioConnection Connection(string callLogPath, string pidFilePath) => new(
        Command: Path.GetFileNameWithoutExtension(System.Environment.ProcessPath) == "dotnet" ? System.Environment.ProcessPath! : "dotnet",
        Arguments: [typeof(OrderMcpStdio).Assembly.Location, Flag],
        WorkingDirectory: Path.GetDirectoryName(callLogPath),
        Environment: new Dictionary<string, string?>
        {
            [CallLogVariable] = callLogPath,
            [PidFileVariable] = pidFilePath,
        });

    /// <summary>The child's own process object, found through the pid file it wrote. The caller disposes it.
    /// Waiting on that object is how a test sees that the child has exited; nothing here stops a process.</summary>
    public static System.Diagnostics.Process OpenChild(string pidFilePath) =>
        System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFilePath)));

    /// <summary>Serves until the client closes stdin. Nothing else may write to stdout, which carries the protocol.</summary>
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var callLog = System.Environment.GetEnvironmentVariable(CallLogVariable);
        if (System.Environment.GetEnvironmentVariable(PidFileVariable) is { Length: > 0 } pidFile)
            await File.WriteAllTextAsync(pidFile, System.Environment.ProcessId.ToString(), ct);

        var options = new SdkServerOptions
        {
            ServerInfo = new Implementation { Name = "orders", Version = "1.0.0" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools() }),
                CallToolHandler = async (context, token) =>
                {
                    var name = context.Params!.Name;
                    if (callLog is not null) await File.AppendAllTextAsync(callLog, name + "\n", token);
                    var orderId = context.Params.Arguments is { } args && args.TryGetValue("orderId", out var id) ? id.GetString() ?? "" : "";
                    var text = name switch
                    {
                        SafeTool => OrderData.Lookup(orderId),
                        CatastrophicTool => "All orders purged.",
                        UnannotatedTool => $"Noted {orderId}.",
                        _ => throw new McpProtocolException($"Unknown tool '{name}'.", McpErrorCode.InvalidParams),
                    };
                    return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
                },
            },
        };
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync(ct);
        return 0;
    }

    static List<Tool> Tools() =>
    [
        new Tool
        {
            Name = SafeTool,
            Description = "Returns the status of an order by its id.",
            InputSchema = Schema,
            Meta = new JsonObject { ["dangerTier"] = "Safe", ["requiresConfirmation"] = false },
        },
        new Tool
        {
            Name = CatastrophicTool,
            Description = "Deletes every order.",
            InputSchema = Schema,
            Meta = new JsonObject { ["dangerTier"] = "Catastrophic", ["requiresConfirmation"] = true },
        },
        new Tool { Name = UnannotatedTool, Description = "Adds a note to an order.", InputSchema = Schema },
    ];
}

/// <summary>
/// Trusts the tier a server declares. That is only sound for a server this program started itself, as the stdio
/// server here; a server from elsewhere should get a verifier that checks something.
/// </summary>
public sealed class DeclaredTierVerifier : IToolManifestVerifier
{
    public VerificationResult Verify(ToolManifest manifest, string sourcePrefix) =>
        new(Trusted: true, AssignedTier: TrustLevel.Trusted, Reason: "server started by this program");
}

/// <summary>
/// An MCP server seen through the governed path: <see cref="McpToolSource"/> lists its tools, and a call goes through
/// <see cref="ToolDispatcher"/> (policy, confirmation, audit) and only then through the transport.
/// </summary>
public sealed class GovernedMcpClient : IAsyncDisposable
{
    static readonly AgentIdentity Caller = new(
        "orders-client", Guid.NewGuid().ToString("N"), ["orders"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    readonly SdkMcpTransport _transport;
    readonly McpToolSource _source;

    GovernedMcpClient(SdkMcpTransport transport, McpToolSource source, ToolDispatcher dispatcher, IReadOnlyList<ToolSurvey> tools)
    {
        _transport = transport;
        _source = source;
        Dispatcher = dispatcher;
        Tools = tools;
    }

    public ToolDispatcher Dispatcher { get; }

    /// <summary>The tools as governance sees them: the declared tier, or the cap when there is no verifier.</summary>
    public IReadOnlyList<ToolSurvey> Tools { get; }

    /// <summary>Lists the server's tools and makes them callable. Takes ownership of <paramref name="transport"/>:
    /// it is disposed here if connecting fails, and by <see cref="DisposeAsync"/> otherwise.</summary>
    public static async Task<GovernedMcpClient> ConnectAsync(
        SdkMcpTransport transport, string serverUri, IToolManifestVerifier? verifier, CancellationToken ct = default)
    {
        try
        {
            var source = new McpToolSource(new McpServerConnection(serverUri, TrustLevel.Unknown), transport, verifier);
            var dispatcher = new ToolDispatcher(new ToolLoader([source]), new InMemoryTokenStore(), new PolicyEngine(), Caller);
            await source.RegisterHandlersAsync(dispatcher, ct);
            var tools = await source.SurveyAsync(ct);
            return new GovernedMcpClient(transport, source, dispatcher, tools);
        }
        catch
        {
            // Ownership has not passed to the caller yet, and a stdio child would otherwise keep running.
            await transport.DisposeAsync();
            throw;
        }
    }

    /// <summary>Calls a tool by the name the server gives it.</summary>
    public Task<ToolResult> CallAsync(string tool, string orderId, CancellationToken ct = default) =>
        Dispatcher.InvokeAsync(
            $"{_source.SourcePrefix}/{tool}",
            new Dictionary<string, JsonElement> { ["orderId"] = JsonSerializer.SerializeToElement(orderId) },
            Caller,
            ct);

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}
