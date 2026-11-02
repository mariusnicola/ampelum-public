using SignalGrid.Ampelum.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using System.Text.Json.Nodes;
using Xunit;

namespace Ampelum.Mcp.Tests;

// ---------------------------------------------------------------------------
// Fake IMcpClientToolSource — used to test mapping logic without a live MCP server
// ---------------------------------------------------------------------------

internal sealed class FakeToolSource(IReadOnlyList<AIFunction> tools) : IMcpClientToolSource
{
    public Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct)
        => Task.FromResult(tools);
}

internal sealed class CapturingToolSource(
    Action<CancellationToken> capture) : IMcpClientToolSource
{
    public Task<IReadOnlyList<AIFunction>> ListToolsAsync(CancellationToken ct)
    {
        capture(ct);
        return Task.FromResult<IReadOnlyList<AIFunction>>([]);
    }
}

// ---------------------------------------------------------------------------
// Fake McpClient subclass — used to exercise the public McpClient code path.
//
// McpClient.McpClient() is experimental API (MCPEXP002); suppressed here in
// test code only. The subclass overrides SendRequestAsync to return a canned
// JSON-RPC response so McpClient.ListToolsAsync() works without a real transport.
// ---------------------------------------------------------------------------

#pragma warning disable MCPEXP002

internal sealed class FakeMcpClient : McpClient
{
    private readonly string _toolsJson;

    public FakeMcpClient(string toolsJson = """{"tools":[]}""")
        => _toolsJson = toolsJson;

    public override Task<ClientCompletionDetails> Completion
        => Task.FromResult(new ClientCompletionDetails());

    public override string? ServerInstructions => null;
    public override ServerCapabilities ServerCapabilities => new();
    public override Implementation ServerInfo => new() { Name = "test", Version = "0" };

    public override string? NegotiatedProtocolVersion => null;
    public override string SessionId => "test-session";
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Returns a canned tools/list JSON-RPC response for any request,
    /// enabling <c>McpClient.ListToolsAsync</c> to work without a real transport.
    /// </summary>
    public override Task<JsonRpcResponse> SendRequestAsync(
        JsonRpcRequest request, CancellationToken ct)
    {
        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonNode.Parse(_toolsJson)
        };
        return Task.FromResult(response);
    }

    public override IAsyncDisposable RegisterNotificationHandler(
        string method,
        Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
        => throw new NotSupportedException("Not needed in tests.");
}

#pragma warning restore MCPEXP002

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class McpToolFactoryTests
{
    // ------------------------------------------------------------------
    // Null guard (public McpClient overload)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FromMcpSessionAsync_NullSession_ThrowsArgumentNullException()
    {
        var ex = await Should.ThrowAsync<ArgumentNullException>(
            () => McpToolFactory.FromMcpSessionAsync((McpClient)null!,
                TestContext.Current.CancellationToken));

        ex.ParamName.ShouldBe("session");
    }

    // ------------------------------------------------------------------
    // IMcpClientToolSource seam — tests tool-mapping logic in isolation
    // ------------------------------------------------------------------

    [Fact]
    public async Task FromMcpSessionAsync_EmptySource_ReturnsEmptyList()
    {
        var source = new FakeToolSource([]);

        var result = await McpToolFactory.FromMcpSessionAsync(
            source, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FromMcpSessionAsync_SingleTool_PreservesNameAndDescription()
    {
        var fn = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("A city")] string city) => "sunny",
            "get_weather",
            "Gets weather for a city");

        var source = new FakeToolSource([fn]);

        var result = await McpToolFactory.FromMcpSessionAsync(
            source, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("get_weather");
        result[0].Description.ShouldBe("Gets weather for a city");
    }

    [Fact]
    public async Task FromMcpSessionAsync_MultipleTools_ReturnsAllTools()
    {
        var fn1 = AIFunctionFactory.Create(
            (string city) => "sunny", "tool_a", "desc a");
        var fn2 = AIFunctionFactory.Create(
            (string query) => "results", "tool_b", "desc b");
        var fn3 = AIFunctionFactory.Create(
            (int x) => x * 2, "tool_c", "desc c");

        var source = new FakeToolSource([fn1, fn2, fn3]);

        var result = await McpToolFactory.FromMcpSessionAsync(
            source, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(3);
        result.Select(f => f.Name).ShouldBe(["tool_a", "tool_b", "tool_c"]);
    }

    [Fact]
    public async Task FromMcpSessionAsync_ForwardsCancellationTokenToSource()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken? capturedToken = null;

        var source = new CapturingToolSource(ct => { capturedToken = ct; });

        await McpToolFactory.FromMcpSessionAsync(source, cts.Token);

        capturedToken.ShouldBe(cts.Token);
    }

    [Fact]
    public async Task FromMcpSessionAsync_ReturnTypeIsReadOnlyList()
    {
        var source = new FakeToolSource([]);

        var result = await McpToolFactory.FromMcpSessionAsync(
            source, TestContext.Current.CancellationToken);

        result.ShouldBeAssignableTo<IReadOnlyList<AIFunction>>();
    }

    // ------------------------------------------------------------------
    // McpClient public overload — exercises the McpClientAdapter bridge
    // ------------------------------------------------------------------

    [Fact]
    public async Task FromMcpSessionAsync_McpClient_EmptyTools_ReturnsEmptyList()
    {
        var client = new FakeMcpClient();

        var result = await McpToolFactory.FromMcpSessionAsync(
            client, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FromMcpSessionAsync_McpClient_MapsToolToAIFunction()
    {
        const string toolsJson = """
            {
              "tools": [
                {
                  "name": "my_tool",
                  "description": "Does something useful",
                  "inputSchema": {
                    "type": "object",
                    "properties": {
                      "param1": { "type": "string" }
                    }
                  }
                }
              ]
            }
            """;

        var client = new FakeMcpClient(toolsJson);

        var result = await McpToolFactory.FromMcpSessionAsync(
            client, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("my_tool");
        result[0].Description.ShouldBe("Does something useful");
    }

    [Fact]
    public async Task FromMcpSessionAsync_McpClient_MultipleTools_AllAreAIFunctions()
    {
        const string toolsJson = """
            {
              "tools": [
                {
                  "name": "alpha",
                  "description": "alpha tool",
                  "inputSchema": { "type": "object" }
                },
                {
                  "name": "beta",
                  "description": "beta tool",
                  "inputSchema": { "type": "object" }
                }
              ]
            }
            """;

        var client = new FakeMcpClient(toolsJson);

        var result = await McpToolFactory.FromMcpSessionAsync(
            client, TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.ShouldAllBe(f => f is AIFunction);
        result.Select(f => f.Name).ShouldBe(["alpha", "beta"]);
    }
}
