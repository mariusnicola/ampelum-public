using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Mcp;

/// <summary>The SDK-backed transport against a real SDK server on in-memory pipes, and the governed path
/// (<see cref="McpToolSource"/> then <see cref="ToolDispatcher"/> then the transport) on top of it.</summary>
public sealed class SdkMcpTransportTests : IAsyncLifetime
{
    private static readonly AgentIdentity Caller = new("sdk-test", "sdk-session", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<(string Name, string Arguments)> _received = [];
    private McpServer? _server;
    private SdkMcpTransport? _transport;

    public ValueTask InitializeAsync()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new Implementation { Name = "pipe-server", Version = "1" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = Tools() }),
                CallToolHandler = (context, _) =>
                {
                    var name = context.Params!.Name;
                    var arguments = JsonSerializer.Serialize(context.Params.Arguments);
                    lock (_received) _received.Add((name, arguments));
                    return ValueTask.FromResult(name == "fails"
                        ? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "it failed" }] }
                        : new CallToolResult { Content = [new TextContentBlock { Text = $"{name}:{arguments}" }] });
                },
            },
        };
        _server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        _ = _server.RunAsync(Ct);
        _transport = new SdkMcpTransport(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _transport!.DisposeAsync();
        await _server!.DisposeAsync();
    }

    private static List<Tool> Tools() =>
    [
        new Tool
        {
            Name = "lookup",
            Description = "Looks a thing up.",
            InputSchema = JsonDocument.Parse("""{"type":"object","properties":{"id":{"type":"string"}}}""").RootElement.Clone(),
            Meta = new JsonObject { ["dangerTier"] = "Safe", ["requiresConfirmation"] = false },
        },
        new Tool
        {
            Name = "wipe",
            Description = "Wipes everything.",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            Meta = new JsonObject { ["dangerTier"] = "Catastrophic", ["requiresConfirmation"] = true },
        },
        new Tool
        {
            Name = "plain",
            Description = "No annotations.",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
        },
        new Tool
        {
            Name = "fails",
            Description = "Always reports an error.",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            Meta = new JsonObject { ["dangerTier"] = "Safe", ["requiresConfirmation"] = false },
        },
    ];

    [Fact]
    public async Task ListTools_MapsDescriptionSchemaAndGovernanceMeta()
    {
        var tools = await _transport!.ListToolsAsync(Ct);

        tools.Select(t => t.Name).ShouldBe(["lookup", "wipe", "plain", "fails"]);
        var lookup = tools.Single(t => t.Name == "lookup");
        lookup.Description.ShouldBe("Looks a thing up.");
        lookup.DangerTier.ShouldBe("Safe");
        lookup.RequiresConfirmation.ShouldBeFalse();
        lookup.InputSchema!.Value.GetProperty("properties").GetProperty("id").GetProperty("type").GetString().ShouldBe("string");
        tools.Single(t => t.Name == "wipe").DangerTier.ShouldBe("Catastrophic");
        var plain = tools.Single(t => t.Name == "plain");
        plain.DangerTier.ShouldBe(string.Empty);
        plain.RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task CallTool_SendsTheArguments_AndMapsTheContent()
    {
        var response = await _transport!.CallToolAsync(
            "lookup", new Dictionary<string, object?> { ["id"] = JsonSerializer.SerializeToElement("A-1") }, Ct);

        response.Success.ShouldBeTrue(response.Error);
        McpContent.Text(response.Result).ShouldBe("""lookup:{"id":"A-1"}""");
        _received.ShouldBe([("lookup", """{"id":"A-1"}""")]);
    }

    [Fact]
    public async Task CallTool_WhenTheServerReportsAnError_IsAFailureWithTheServersText()
    {
        var response = await _transport!.CallToolAsync("fails", new Dictionary<string, object?>(), Ct);

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe("it failed");
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndLaterCallsThrow()
    {
        await _transport!.ListToolsAsync(Ct);

        await _transport.DisposeAsync();
        await _transport.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => _transport.ListToolsAsync(Ct));
        await Should.ThrowAsync<ObjectDisposedException>(() => _transport.CallToolAsync("lookup", new Dictionary<string, object?>(), Ct));
    }

    [Fact]
    public async Task Governed_SafeToolRunsThroughTheDispatcher_AndTheResultIsTheServersText()
    {
        var (dispatcher, names) = await GovernedAsync(new AcceptingVerifier());

        var result = await dispatcher.InvokeAsync(
            "mcp/pipe/lookup", Args("""{"id":"A-2"}"""), Caller, Ct);

        names.ShouldContain("mcp/pipe/lookup");
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldBe("""lookup:{"id":"A-2"}""");
        _received.ShouldBe([("lookup", """{"id":"A-2"}""")]);
    }

    [Fact]
    public async Task Governed_ADeclaredCatastrophicTool_IsBlocked_AndTheServerNeverReceivesTheCall()
    {
        var (dispatcher, _) = await GovernedAsync(new AcceptingVerifier());

        var result = await dispatcher.InvokeAsync("mcp/pipe/wipe", Args("{}"), Caller, Ct);

        result.Success.ShouldBeFalse();
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governed_AToolWithoutAnnotation_IsBlocked_AndTheServerNeverReceivesTheCall()
    {
        var (dispatcher, _) = await GovernedAsync(new AcceptingVerifier());

        var result = await dispatcher.InvokeAsync("mcp/pipe/plain", Args("{}"), Caller, Ct);

        result.Success.ShouldBeFalse();
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governed_WithoutAVerifier_EvenADeclaredSafeToolIsBlocked_AndTheServerNeverReceivesTheCall()
    {
        var (dispatcher, _) = await GovernedAsync(verifier: null);

        var result = await dispatcher.InvokeAsync("mcp/pipe/lookup", Args("""{"id":"A-3"}"""), Caller, Ct);

        result.Success.ShouldBeFalse();
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Governed_AToolErrorFromTheServer_IsAFailedResult()
    {
        var (dispatcher, _) = await GovernedAsync(new AcceptingVerifier());

        var result = await dispatcher.InvokeAsync("mcp/pipe/fails", Args("{}"), Caller, Ct);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("it failed");
        _received.Select(r => r.Name).ShouldBe(["fails"]);
    }

    [Fact]
    public async Task Governed_AToolTheVerifierRejects_HasNoHandler_AndTheServerNeverReceivesACall()
    {
        var (dispatcher, names) = await GovernedAsync(new RejectingVerifier("plain"));

        var result = await dispatcher.InvokeAsync("mcp/pipe/plain", Args("{}"), Caller, Ct);

        names.ShouldNotContain("mcp/pipe/plain");
        names.ShouldContain("mcp/pipe/lookup");
        dispatcher.HasHandler("mcp/pipe/plain").ShouldBeFalse();
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("Unknown tool");
        _received.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterHandlers_ForASecondServerOnTheSameHost_RefusesToReplaceTheFirstServersHandlers()
    {
        var first = new McpToolSource(new McpServerConnection("http://127.0.0.1:1/mcp", TrustLevel.Trusted), _transport, new AcceptingVerifier());
        var second = new McpToolSource(new McpServerConnection("http://127.0.0.1:2/mcp", TrustLevel.Trusted), _transport, new AcceptingVerifier());
        first.SourcePrefix.ShouldBe(second.SourcePrefix);
        var dispatcher = new ToolDispatcher(new ToolLoader([first, second]), new InMemoryTokenStore(), new PolicyEngine(), Caller);
        await first.RegisterHandlersAsync(dispatcher, Ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => second.RegisterHandlersAsync(dispatcher, Ct));

        ex.Message.ShouldContain("http://127.0.0.1:1/mcp");
        ex.Message.ShouldContain("http://127.0.0.1:2/mcp");
        ex.Message.ShouldContain("mcp/127-0-0-1/lookup");
        var result = await dispatcher.InvokeAsync("mcp/127-0-0-1/lookup", Args("""{"id":"A-9"}"""), Caller, Ct);
        result.Success.ShouldBeTrue(result.Error);
        _received.ShouldBe([("lookup", """{"id":"A-9"}""")]);
    }

    [Fact]
    public async Task RegisterHandlers_RefusesAHandlerRegisteredByHand_ButASecondCallFromTheSameSourceIsAllowed()
    {
        var source = new McpToolSource(new McpServerConnection("mcp://pipe", TrustLevel.Trusted), _transport, new AcceptingVerifier());
        var dispatcher = new ToolDispatcher(new ToolLoader([source]), new InMemoryTokenStore(), new PolicyEngine(), Caller);
        dispatcher.RegisterHandler("mcp/pipe/wipe", (_, _) => Task.FromResult(new ToolResult(true, "mine", null, PolicyDecision.Allowed)));

        await Should.ThrowAsync<InvalidOperationException>(() => source.RegisterHandlersAsync(dispatcher, Ct));

        var other = new McpToolSource(new McpServerConnection("mcp://pipe2", TrustLevel.Trusted), _transport, new AcceptingVerifier());
        var clean = new ToolDispatcher(new ToolLoader([other]), new InMemoryTokenStore(), new PolicyEngine(), Caller);
        var once = await other.RegisterHandlersAsync(clean, Ct);
        var twice = await other.RegisterHandlersAsync(clean, Ct);
        twice.ShouldBe(once);
    }

    [Fact]
    public async Task RegisterHandlers_OnAManifestOnlySource_Throws()
    {
        var source = new McpToolSource(new McpServerConnection("mcp://pipe", TrustLevel.Trusted));
        var dispatcher = new ToolDispatcher(new ToolLoader([source]), new InMemoryTokenStore(), new PolicyEngine(), Caller);

        await Should.ThrowAsync<InvalidOperationException>(() => source.RegisterHandlersAsync(dispatcher, Ct));
    }

    [Fact]
    public async Task CreateHttp_SendsTheKeyAsABearerToken_AndAFailedConnectionIsATransportException()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await using var transport = SdkMcpTransport.CreateHttp(new McpServerConnection("http://127.0.0.1:1/mcp", TrustLevel.Trusted, "the-key"), http);

        await Should.ThrowAsync<McpTransportException>(() => transport.ListToolsAsync(Ct));

        handler.Authorization.ShouldBe("Bearer the-key");
    }

    private async Task<(ToolDispatcher Dispatcher, IReadOnlyList<string> Names)> GovernedAsync(IToolManifestVerifier? verifier)
    {
        var source = new McpToolSource(new McpServerConnection("mcp://pipe", TrustLevel.Trusted), _transport, verifier);
        var dispatcher = new ToolDispatcher(new ToolLoader([source]), new InMemoryTokenStore(), new PolicyEngine(), Caller);
        var names = await source.RegisterHandlersAsync(dispatcher, Ct);
        return (dispatcher, names);
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private sealed class AcceptingVerifier : IToolManifestVerifier
    {
        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix) => new(Trusted: true, AssignedTier: TrustLevel.Trusted);
    }

    private sealed class RejectingVerifier(string rejected) : IToolManifestVerifier
    {
        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix) =>
            new(Trusted: manifest.Name != rejected, AssignedTier: TrustLevel.Trusted);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization ??= request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
