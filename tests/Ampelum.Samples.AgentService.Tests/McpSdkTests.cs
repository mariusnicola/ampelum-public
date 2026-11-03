using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>
/// The governed MCP path on the SDK transport: a child process over stdio and the sample's server over HTTP.
/// A blocked call is proved by the server's own record of calls it received, not by the result alone.
/// </summary>
public sealed class McpSdkTests : IDisposable
{
    const string ShippedText = "Order A-1001: Shipped, carrier NorthPost, delivery in 2 business days.";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ampelum-agent-service-mcp", Guid.NewGuid().ToString("N"));

    public McpSdkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A failing test can leave the child running with its working directory here; the folder is in temp.
        }
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string CallLog => Path.Combine(_dir, "calls.log");

    string PidFile => Path.Combine(_dir, "child.pid");

    string[] ServerSaw() => File.Exists(CallLog) ? File.ReadAllLines(CallLog) : [];

    SdkMcpTransport Stdio() => SdkMcpTransport.CreateStdio(OrderMcpStdio.Connection(CallLog, PidFile));

    [Fact]
    public async Task Stdio_ListsTheChildsTools_WithTheTiersItDeclares()
    {
        await using var client = await GovernedMcpClient.ConnectAsync(Stdio(), OrderMcpStdio.ServerUri, new DeclaredTierVerifier(), Ct);

        client.Tools.Select(t => (t.Name, t.DangerTier, t.RequiresConfirmation)).ShouldBe(
        [
            (OrderMcpStdio.SafeTool, DangerTier.Safe, false),
            (OrderMcpStdio.CatastrophicTool, DangerTier.Catastrophic, true),
            (OrderMcpStdio.UnannotatedTool, DangerTier.Catastrophic, true),
        ]);
    }

    [Fact]
    public async Task Stdio_ASafeToolRunsThroughTheDispatcher_WithItsArguments_AndAnythingElseIsBlockedBeforeTheServer()
    {
        System.Diagnostics.Process child;
        await using (var client = await GovernedMcpClient.ConnectAsync(Stdio(), OrderMcpStdio.ServerUri, new DeclaredTierVerifier(), Ct))
        {
            child = OrderMcpStdio.OpenChild(PidFile);

            var safe = await client.CallAsync(OrderMcpStdio.SafeTool, "A-1001", Ct);
            var other = await client.CallAsync(OrderMcpStdio.SafeTool, "A-1002", Ct);
            var catastrophic = await client.CallAsync(OrderMcpStdio.CatastrophicTool, "A-1001", Ct);
            var unannotated = await client.CallAsync(OrderMcpStdio.UnannotatedTool, "A-1001", Ct);

            safe.Success.ShouldBeTrue(safe.Error);
            safe.Output.ShouldBe(ShippedText);
            other.Output.ShouldBe("Order A-1002: Processing, carrier none yet, delivery in unknown.");
            catastrophic.Success.ShouldBeFalse();
            unannotated.Success.ShouldBeFalse();
            var audit = await client.Dispatcher.AuditLog.QueryAsync(ct: Ct);
            audit.Select(e => e.EventType).ShouldContain("invocation.blocked");
            audit.Count(e => e.EventType == "handler.succeeded").ShouldBe(2);
        }
        using (child)
        {
            child.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue("the child must end because the transport was disposed");
        }

        ServerSaw().ShouldBe([OrderMcpStdio.SafeTool, OrderMcpStdio.SafeTool]);
    }

    [Fact]
    public async Task Stdio_WithoutAVerifier_EvenTheDeclaredSafeToolIsBlocked_AndTheChildNeverReceivesACall()
    {
        System.Diagnostics.Process child;
        await using (var client = await GovernedMcpClient.ConnectAsync(Stdio(), OrderMcpStdio.ServerUri, verifier: null, Ct))
        {
            child = OrderMcpStdio.OpenChild(PidFile);

            var result = await client.CallAsync(OrderMcpStdio.SafeTool, "A-1001", Ct);

            result.Success.ShouldBeFalse();
            client.Tools.ShouldAllBe(t => t.DangerTier == DangerTier.Catastrophic);
        }
        using (child)
        {
            child.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }

        ServerSaw().ShouldBeEmpty();
    }

    [Fact]
    public async Task Stdio_WhenConnectingFailsAfterTheChildStarted_TheTransportIsDisposedAndTheChildEnds()
    {
        var verifier = new ThrowingVerifier(PidFile);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            GovernedMcpClient.ConnectAsync(Stdio(), OrderMcpStdio.ServerUri, verifier, Ct));

        using var child = verifier.Child.ShouldNotBeNull();
        child.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue("a failed connect must not leave the child running");
    }

    sealed class ThrowingVerifier(string pidFile) : IToolManifestVerifier
    {
        public System.Diagnostics.Process? Child { get; private set; }

        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix)
        {
            Child ??= OrderMcpStdio.OpenChild(pidFile);
            throw new InvalidOperationException("verifier failed");
        }
    }

    [Fact]
    public async Task Stdio_DisposingTheTransportEndsTheChildProcess()
    {
        var transport = Stdio();
        await transport.ListToolsAsync(Ct);
        using var child = OrderMcpStdio.OpenChild(PidFile);
        child.HasExited.ShouldBeFalse();

        await transport.DisposeAsync();

        child.WaitForExit(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    [Fact]
    public async Task Http_ASafeToolRunsThroughTheDispatcher_AndWithoutAVerifierItIsBlockedBeforeTheServer()
    {
        var server = new OrderMcpServer();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var app = server.Map(builder.Build());
        await app.StartAsync(Ct);
        using var http = app.GetTestClient();
        const string uri = "http://localhost" + OrderMcpServer.Route;

        await using (var capped = await GovernedMcpClient.ConnectAsync(
            SdkMcpTransport.CreateHttp(new McpServerConnection(uri, TrustLevel.Unknown), http), uri, verifier: null, Ct))
        {
            var blocked = await capped.CallAsync(OrderMcpServer.ToolName, "A-1001", Ct);

            blocked.Success.ShouldBeFalse();
            server.ReceivedToolCalls.ShouldBeEmpty();
        }
        await using (var trusting = await GovernedMcpClient.ConnectAsync(
            SdkMcpTransport.CreateHttp(new McpServerConnection(uri, TrustLevel.Unknown), http), uri, new DeclaredTierVerifier(), Ct))
        {
            var allowed = await trusting.CallAsync(OrderMcpServer.ToolName, "A-1001", Ct);

            allowed.Success.ShouldBeTrue(allowed.Error);
            allowed.Output.ShouldBe(ShippedText);
        }

        server.ReceivedToolCalls.ShouldBe([OrderMcpServer.ToolName]);
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task Http_OverARealLoopbackSocket_ListsAndCallsTheTool()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var server = new OrderMcpServer();
        await using var app = server.Map(builder.Build());
        await app.StartAsync(Ct);
        var address = new Uri(app.Urls.Single());
        var uri = new Uri(address, OrderMcpServer.Route).ToString();
        try
        {
            await using var client = await GovernedMcpClient.ConnectAsync(
                SdkMcpTransport.CreateHttp(new McpServerConnection(uri, TrustLevel.Unknown)), uri, new DeclaredTierVerifier(), Ct);

            var result = await client.CallAsync(OrderMcpServer.ToolName, "A-1002", Ct);

            address.Host.ShouldBe("127.0.0.1");
            client.Tools.ShouldHaveSingleItem().Name.ShouldBe(OrderMcpServer.ToolName);
            result.Output.ShouldBe("Order A-1002: Processing, carrier none yet, delivery in unknown.");
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }
}
