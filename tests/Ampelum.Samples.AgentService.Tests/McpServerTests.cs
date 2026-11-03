using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>The sample's MCP server, called by the library's own HTTP client transport and by the sample's tool agent.</summary>
public sealed class McpServerTests : IAsyncLifetime
{
    const string ShippedText = "Order A-1001: Shipped, carrier NorthPost, delivery in 2 business days.";

    readonly OrderMcpServer _server = new();
    WebApplication? _app;
    HttpClient? _http;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    HttpClient Http => _http ?? throw new InvalidOperationException("Not initialized.");

    HttpMcpTransport Transport() => new(new McpServerConnection("http://localhost" + OrderMcpServer.Route, TrustLevel.Unknown), Http);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = _server.Map(builder.Build());
        await _app.StartAsync(Ct);
        _http = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ToolsList_OverHttp_ReturnsTheToolWithItsSchemaAndTheTierItDeclares()
    {
        var tools = await Transport().ListToolsAsync(Ct);

        var tool = tools.ShouldHaveSingleItem();
        tool.Name.ShouldBe(OrderMcpServer.ToolName);
        tool.Description.ShouldBe("Returns the status of an order by its id.");
        tool.DangerTier.ShouldBe(nameof(DangerTier.Safe));
        tool.RequiresConfirmation.ShouldBeFalse();
        tool.InputSchema!.Value.GetProperty("properties").GetProperty("orderId").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task ToolsCall_OverHttp_RunsTheToolThroughTheDispatcher()
    {
        var call = await Transport().CallToolAsync(
            OrderMcpServer.ToolName, new Dictionary<string, object?> { ["orderId"] = "A-1001" }, Ct);

        call.Success.ShouldBeTrue(call.Error);
        TransportToolSource.Text(call.Result).ShouldBe(ShippedText);
        var audit = await _server.Dispatcher.AuditLog.QueryAsync(OrderMcpServer.ToolName, ct: Ct);
        audit.ShouldContain(e => e.EventType == "invocation.completed" && e.Result!.Output == ShippedText);
    }

    [Fact]
    public async Task ToolsCall_ForAnOrderThatDoesNotExist_ReturnsTheToolsOwnAnswer()
    {
        var call = await Transport().CallToolAsync(
            OrderMcpServer.ToolName, new Dictionary<string, object?> { ["orderId"] = "A-9999" }, Ct);

        call.Success.ShouldBeTrue();
        TransportToolSource.Text(call.Result).ShouldBe("No order with id A-9999.");
    }

    [Fact]
    public async Task ToolsCall_ForAnUnknownTool_IsReportedAsAToolError_AndAuditedAsBlocked()
    {
        var call = await Transport().CallToolAsync("no_such_tool", new Dictionary<string, object?>(), Ct);

        call.Success.ShouldBeFalse();
        call.Error.ShouldNotBeNull().ShouldContain("Unknown tool");
        var audit = await _server.Dispatcher.AuditLog.QueryAsync("no_such_tool", ct: Ct);
        audit.ShouldContain(e => e.EventType == "policy.blocked");
    }

    [Fact]
    public async Task Survey_OfTheServedTools_IgnoresTheSafeTierTheServerDeclares()
    {
        var surveyed = await ToolAgent.SurveyAsync(Transport(), "http://localhost" + OrderMcpServer.Route, Ct);

        var tool = surveyed.ShouldHaveSingleItem();
        tool.Name.ShouldBe(OrderMcpServer.ToolName);
        tool.DangerTier.ShouldBe(DangerTier.Catastrophic);
        tool.RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task ToolAgent_UsesTheServedTool_WithToolsListThenToolsCall()
    {
        var model = FakeScript.ToolModel("A-1001");
        using var agent = new ToolAgent(model, new TransportToolSource(Transport()));

        var answer = await agent.AskAsync("Where is order A-1001?", Ct);

        answer.ShouldBe(ShippedText);
        model.Inner.RecordedCalls[0].Options!.Tools!.Select(t => t.Name).ShouldBe([OrderMcpServer.ToolName]);
        var audit = await _server.Dispatcher.AuditLog.QueryAsync(OrderMcpServer.ToolName, ct: Ct);
        audit.Count(e => e.EventType == "invocation.completed").ShouldBe(1);
    }

    [Fact]
    public async Task Protocol_AnswersAnUnknownMethodAndBrokenJsonWithJsonRpcErrors_AndAcceptsANotification()
    {
        using var unknown = await Http.PostAsync(OrderMcpServer.Route, Json("""{"jsonrpc":"2.0","id":7,"method":"resources/list"}"""), Ct);
        using var broken = await Http.PostAsync(OrderMcpServer.Route, Json("{not json"), Ct);
        using var notification = await Http.PostAsync(OrderMcpServer.Route, Json("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""), Ct);

        var unknownError = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct)).RootElement;
        unknownError.GetProperty("id").GetInt32().ShouldBe(7);
        unknownError.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        JsonDocument.Parse(await broken.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
        notification.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ToolsList_OverARealLoopbackSocket_ReturnsTheTool()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var app = new OrderMcpServer().Map(builder.Build());
        await app.StartAsync(Ct);
        try
        {
            var address = new Uri(app.Urls.Single());
            using var http = new HttpClient();
            var transport = new HttpMcpTransport(new McpServerConnection(new Uri(address, OrderMcpServer.Route).ToString(), TrustLevel.Unknown), http);

            var tools = await transport.ListToolsAsync(Ct);
            var call = await transport.CallToolAsync(
                OrderMcpServer.ToolName, new Dictionary<string, object?> { ["orderId"] = "A-1002" }, Ct);

            address.Host.ShouldBe("127.0.0.1");
            tools.Single().Name.ShouldBe(OrderMcpServer.ToolName);
            TransportToolSource.Text(call.Result).ShouldBe("Order A-1002: Processing, carrier none yet, delivery in unknown.");
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }
}
