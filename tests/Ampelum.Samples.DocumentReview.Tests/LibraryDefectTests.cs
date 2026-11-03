using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agent.A2A;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Hybrid;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

/// <summary>
/// Pins the library behaviour the sample README lists as defects. These tests pass while the defect
/// exists; when one fails, the library changed and the README line must go.
/// </summary>
public sealed class LibraryDefectTests
{
    sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    static DefaultAgentCardVerifier NewVerifier(out DefaultAgentIdentityProvider issuer)
    {
        issuer = new DefaultAgentIdentityProvider(RandomNumberGenerator.GetBytes(32));
        return new DefaultAgentCardVerifier(issuer);
    }

    static A2AAgentCard CardWith(IReadOnlyList<A2AAgentCapability>? capabilities, IReadOnlyList<A2AAuthenticationScheme>? schemes = null) => new()
    {
        Name = "supplier-clause-reviewer",
        Description = "d",
        Version = "1",
        Url = "http://127.0.0.1:1/rpc",
        Capabilities = capabilities,
        AuthenticationSchemes = schemes,
    };

    [Fact]
    public void CardVerifier_ReturnsNoWarningsWhateverTheCapabilitiesSay()
    {
        var verifier = NewVerifier(out _);
        var hostile = new A2AAgentCapability
        {
            Streaming = true,
            PushNotifications = true,
            StateSync = true,
            LongRunningTasks = true,
            Custom = new Dictionary<string, object?> { ["exec"] = "rm -rf /", ["scope"] = "*", ["nested"] = new { anything = 1 } },
        };

        verifier.VerifyCapabilities(CardWith([hostile, hostile])).ShouldBeEmpty();
        verifier.VerifyCapabilities(CardWith([])).ShouldBeEmpty();
        verifier.VerifyCapabilities(CardWith(null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CardVerifier_AcceptsATokenIssuedForAnotherAgent()
    {
        var verifier = NewVerifier(out var issuer);
        var jwt = await issuer.IssueAsync("payroll-agent", ["payments"], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var card = CardWith(null, [new A2AAuthenticationScheme { Scheme = "bearer", Credentials = jwt.Token }]);

        var identity = await verifier.VerifyIdentityAsync(card, TestContext.Current.CancellationToken);

        identity.ShouldNotBeNull();
        identity.AgentType.ShouldBe("payroll-agent");
        card.Name.ShouldNotBe(identity.AgentType);
    }

    [Fact]
    public async Task CardVerifier_SchemeNameIsMatchedCaseSensitively()
    {
        var verifier = NewVerifier(out var issuer);
        var jwt = await issuer.IssueAsync("a", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var card = CardWith(null, [new A2AAuthenticationScheme { Scheme = "Bearer", Credentials = jwt.Token }]);

        (await verifier.VerifyIdentityAsync(card, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task A2AClient_FailsOnStringStatesThatOtherAgentsSend()
    {
        const string stateAsString = """
            {"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","state":"completed","messages":[],
            "createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}}}
            """;
        using var client = new A2AClient(new HttpClient(new StubHandler(stateAsString)));
        var message = new A2AMessage { Id = "m", Role = MessageRole.User, Parts = [new A2APart { Text = "x" }] };

        await Should.ThrowAsync<JsonException>(() => client.SendMessageAsync("http://127.0.0.1:1/rpc", message, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A2AClient_FailsOnStringRolesThatOtherAgentsSend()
    {
        const string roleAsString = """
            {"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","state":3,
            "messages":[{"id":"m","role":"agent","parts":[{"text":"done"}]}],
            "createdAt":"2026-01-01T00:00:00+00:00","updatedAt":"2026-01-01T00:00:00+00:00"}}}
            """;
        using var client = new A2AClient(new HttpClient(new StubHandler(roleAsString)));
        var message = new A2AMessage { Id = "m", Role = MessageRole.User, Parts = [new A2APart { Text = "x" }] };

        await Should.ThrowAsync<JsonException>(() => client.SendMessageAsync("http://127.0.0.1:1/rpc", message, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A2AClient_FailsWhenATaskCarriesNoTimestamps()
    {
        const string noTimestamps = """{"jsonrpc":"2.0","id":"1","result":{"task":{"id":"t","state":3,"messages":[]}}}""";
        using var client = new A2AClient(new HttpClient(new StubHandler(noTimestamps)));
        var message = new A2AMessage { Id = "m", Role = MessageRole.User, Parts = [new A2APart { Text = "x" }] };

        await Should.ThrowAsync<JsonException>(() => client.SendMessageAsync("http://127.0.0.1:1/rpc", message, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A2AClient_DoesNotCheckThatTheCardNamesTheHostItCameFrom()
    {
        const string card = """{"name":"n","description":"d","version":"1","url":"http://203.0.113.9/steal"}""";
        using var client = new A2AClient(new HttpClient(new StubHandler(card)));

        var fetched = await client.FetchAgentCardAsync("http://127.0.0.1:1", TestContext.Current.CancellationToken);

        fetched!.Url.ShouldBe("http://203.0.113.9/steal");
    }

    [Fact]
    public async Task A2AClient_SendsNumbersForRoleAndNotTheNamesOtherAgentsExpect()
    {
        var seen = new List<string>();
        var handler = new CapturingHandler(seen);
        using var client = new A2AClient(new HttpClient(handler));
        var message = new A2AMessage { Id = "m", Role = MessageRole.User, Parts = [new A2APart { Text = "x" }] };

        try
        {
            await client.SendMessageAsync("http://127.0.0.1:1/rpc", message, ct: TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException)
        {
        }

        using var sent = JsonDocument.Parse(seen.ShouldHaveSingleItem());
        sent.RootElement.GetProperty("params").GetProperty("message").GetProperty("role").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    sealed class CapturingHandler(List<string> seen) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Rerankers_ThrowRawExceptionsOnAnIndexOutsideTheCandidates()
    {
        var docs = new List<RetrievedDocument> { new("a", "text", 0, new Dictionary<string, object?>()) };
        var cohere = new CohereReranker(new HttpClient(new StubHandler("""{"results":[{"index":7,"relevance_score":0.9}]}""")), new CohereRerankerOptions { ApiKey = "k", Model = "m" });
        var voyage = new VoyageReranker(new HttpClient(new StubHandler("""{"data":[{"index":7,"relevance_score":0.9}]}""")), new VoyageRerankerOptions { ApiKey = "k", Model = "m" });

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => cohere.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => voyage.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rerankers_ThrowNullReferenceOnAnEmptyJsonBody()
    {
        var docs = new List<RetrievedDocument> { new("a", "text", 0, new Dictionary<string, object?>()) };
        var cohere = new CohereReranker(new HttpClient(new StubHandler("null")), new CohereRerankerOptions { ApiKey = "k", Model = "m" });
        var voyage = new VoyageReranker(new HttpClient(new StubHandler("null")), new VoyageRerankerOptions { ApiKey = "k", Model = "m" });

        await Should.ThrowAsync<NullReferenceException>(() => cohere.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<NullReferenceException>(() => voyage.RerankAsync("q", docs, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TaskExecutor_SendsItsOutgoingRequestWithTheAgentRole()
    {
        var seen = new List<string>();
        using var client = new A2AClient(new HttpClient(new CapturingHandler(seen)));
        var executor = new DefaultA2ATaskExecutor(client);
        var node = new TaskNode
        {
            Id = "n",
            TaskType = "a2a/external",
            Description = "review this",
            Metadata = new Dictionary<string, object?> { ["agentUrl"] = "http://127.0.0.1:1/rpc" },
        };

        try
        {
            await executor.ExecuteAsync(node, TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException)
        {
        }

        using var sent = JsonDocument.Parse(seen.ShouldHaveSingleItem());
        sent.RootElement.GetProperty("params").GetProperty("message").GetProperty("role").GetInt32().ShouldBe((int)MessageRole.Agent);
    }
}
