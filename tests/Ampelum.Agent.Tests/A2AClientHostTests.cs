using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Agent.A2A;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class A2AClientHostTests
{
    sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    static string Card(string url) => $$"""{"name":"n","description":"d","version":"1","url":"{{url}}"}""";

    static string TaskReply(string state, string role) => $$$$"""
        {"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","state":{{{{state}}}},
        "messages":[{"id":"m","role":{{{{role}}}},"parts":[{"text":"done"}]}],
        "createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}}}
        """;

    static A2AMessage Message() => new() { Id = "m", Role = MessageRole.User, Parts = [new A2APart { Text = "x" }] };

    [Theory]
    [InlineData("http://agents.example.com/rpc")]
    [InlineData("http://agents.example.com:80/rpc")]
    [InlineData("http://AGENTS.example.com/other/path")]
    public async Task FetchAgentCardAsync_ACardNamingTheOriginItCameFrom_IsReturned(string cardUrl)
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(Card(cardUrl))));

        var card = await client.FetchAgentCardAsync("http://agents.example.com", TestContext.Current.CancellationToken);

        card!.Url.ShouldBe(cardUrl);
    }

    [Theory]
    [InlineData("http://203.0.113.9/steal")]
    [InlineData("http://agents.example.com.evil.test/rpc")]
    [InlineData("http://agents.example.com:8080/rpc")]
    [InlineData("https://agents.example.com/rpc")]
    [InlineData("/rpc")]
    [InlineData("not a url")]
    public async Task FetchAgentCardAsync_ACardNamingAnotherOrigin_IsRefused(string cardUrl)
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(Card(cardUrl))));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            client.FetchAgentCardAsync("http://agents.example.com", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"completed\"", "\"agent\"")]
    [InlineData("\"Completed\"", "\"Agent\"")]
    [InlineData("3", "1")]
    public async Task SendMessageAsync_StatesAndRoles_ReadAsNamesOrAsNumbers(string state, string role)
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(TaskReply(state, role))));

        var response = await client.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken);

        var task = response.Result!.Task!;
        task.State.ShouldBe(TaskState.Completed);
        task.Messages.Single().Role.ShouldBe(MessageRole.Agent);
    }

    [Theory]
    [InlineData("\"input-required\"", TaskState.InputRequired)]
    [InlineData("\"input_required\"", TaskState.InputRequired)]
    [InlineData("\"canceled\"", TaskState.Canceled)]
    public async Task SendMessageAsync_AStateNameWithADashOrUnderscore_ReadsAsTheState(string state, TaskState expected)
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(TaskReply(state, "1"))));

        var response = await client.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken);

        response.Result!.Task!.State.ShouldBe(expected);
    }

    [Theory]
    [InlineData("\"finished\"", "1")]
    [InlineData("3", "\"robot\"")]
    public async Task SendMessageAsync_AnUnknownStateOrRoleName_StillFails(string state, string role)
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(TaskReply(state, role))));

        await Should.ThrowAsync<JsonException>(() =>
            client.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"Working,Completed\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"--1\"")]
    [InlineData("\"1\"")]
    [InlineData("\"\"")]
    [InlineData("\" completed\"")]
    public async Task SendMessageAsync_AStringThatIsNotAnEnumName_Fails_ForStateAndForRole(string text)
    {
        using var asState = new A2AClient(new HttpClient(new StubHandler(TaskReply(text, "1"))));
        using var asRole = new A2AClient(new HttpClient(new StubHandler(TaskReply("3", text))));

        await Should.ThrowAsync<JsonException>(() =>
            asState.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken));
        await Should.ThrowAsync<JsonException>(() =>
            asRole.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendMessageAsync_ANumber_IsStillReadWhateverItIs()
    {
        using var client = new A2AClient(new HttpClient(new StubHandler(TaskReply("42", "7"))));

        var response = await client.SendMessageAsync("http://127.0.0.1:1/rpc", Message(), ct: TestContext.Current.CancellationToken);

        ((int)response.Result!.Task!.State).ShouldBe(42);
        ((int)response.Result.Task.Messages.Single().Role).ShouldBe(7);
    }

    // Two real listeners on the loopback interface, each answering with what the test gives it.
    sealed class LocalServer : IDisposable
    {
        readonly HttpListener _listener = new();
        readonly CancellationTokenSource _stop = new();

        public int Port { get; }

        public string Origin => $"http://127.0.0.1:{Port}";

        public LocalServer(Func<HttpListenerRequest, (int Status, string? Location, string Body)> answer)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    var (status, location, body) = answer(context.Request);
                    context.Response.StatusCode = status;
                    if (location is not null) context.Response.RedirectLocation = location;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }

    [Fact]
    public async Task FetchAgentCardAsync_ACardServedByAnotherOriginAfterARedirect_IsRefused_EvenWhenItNamesTheBaseOrigin()
    {
        // The server that is asked redirects to a second server, whose card names the first one.
        var askedOrigin = "";
        using var serving = new LocalServer(_ => (200, null, Card(askedOrigin + "/rpc")));
        using var asked = new LocalServer(_ => (302, serving.Origin + A2AMethods.AgentCardPath, ""));
        askedOrigin = asked.Origin;
        using var client = new A2AClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            client.FetchAgentCardAsync(asked.Origin, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchAgentCardAsync_ARedirectThatStaysOnTheBaseOrigin_IsFollowed()
    {
        var origin = "";
        using var server = new LocalServer(request => request.Url!.AbsolutePath == A2AMethods.AgentCardPath
            ? (302, origin + "/moved/card", "")
            : (200, null, Card(origin + "/rpc")));
        origin = server.Origin;
        using var client = new A2AClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }));

        var card = await client.FetchAgentCardAsync(server.Origin, TestContext.Current.CancellationToken);

        card!.Url.ShouldBe(server.Origin + "/rpc");
    }

    [Fact]
    public async Task Dispose_LeavesAnInjectedHttpClientUsable()
    {
        var handler = new StubHandler(Card("http://agents.example.com/rpc"));
        using var http = new HttpClient(handler);
        var client = new A2AClient(http);

        client.Dispose();

        var response = await http.GetAsync("http://agents.example.com/x", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Requests.ShouldBe(1);
    }
}
