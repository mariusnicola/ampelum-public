using Shouldly;
using Xunit;
using System.Net;
using System.Text.Json;
using SignalGrid.Ampelum.Agent.A2A;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class A2AIntegrationTests
{
    // ── Agent card JWT verification ─────────────────────────────────

    [Fact]
    public async Task VerifyAsync_NullToken_ReturnsNull()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var identity = await verifier.VerifyAsync(null!);
        identity.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_EmptyToken_ReturnsNull()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var identity = await verifier.VerifyAsync("");
        identity.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_ExpiredToken_ReturnsNull()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("test-agent", new[] { "read" }, TimeSpan.Zero);

        var identity = await verifier.VerifyAsync(jwt.Token);
        identity.ShouldBeNull(); // expired
    }

    [Fact]
    public async Task VerifyAsync_ValidToken_ReturnsAgentIdentity()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("code-agent", new[] { "read", "write" }, TimeSpan.FromHours(1));

        var identity = await verifier.VerifyAsync(jwt.Token);
        identity.ShouldNotBeNull();
        identity!.AgentType.ShouldBe("code-agent");
        identity.Scope.ShouldContain("read");
        identity.Scope.ShouldContain("write");
        identity.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task VerifyAsync_InvalidSignature_ReturnsNull()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var badToken = "eyJhbGciOiJIUzI1NiJ9.eyJhZ2VudFR5cGUiOiJ4In0.invalidsignature";

        var identity = await verifier.VerifyAsync(badToken);
        identity.ShouldBeNull();
    }

    // ── Capability matching ────────────────────────────────────────

    [Fact]
    public async Task VerifyCapabilitiesAsync_AllRequiredPresent_ReturnsSuccess()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("code-agent", new[] { "read", "write" }, TimeSpan.FromHours(1));

        var result = await verifier.VerifyCapabilitiesAsync(
            jwt.Claims,
            new[] { "read", "write" });

        result.AllRequiredPresent.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task VerifyCapabilitiesAsync_MissingCapability_ReturnsWarning()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("code-agent", new[] { "read" }, TimeSpan.FromHours(1));

        var result = await verifier.VerifyCapabilitiesAsync(
            jwt.Claims,
            new[] { "read", "write", "deploy" });

        result.AllRequiredPresent.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Contains("write"));
        result.Warnings.ShouldContain(w => w.Contains("deploy"));
    }

    // ── A2A task delegation ────────────────────────────────────────

    [Fact]
    public async Task DefaultA2ATaskExecutor_ExecuteAsync_CallsExternalAgent()
    {
        var mockTask = new A2ATask
        {
            Id = "task-123",
            State = TaskState.Completed,
            Messages = new MessageCollection(new[]
            {
                new A2AMessage
                {
                    Id = "msg-1",
                    Role = MessageRole.Agent,
                    Parts = new[]
                    {
                        new A2APart { Text = "Task completed successfully" },
                    },
                }
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var mockResponse = new A2AResponse
        {
            Result = new A2AResult { Task = mockTask },
            Id = "req-1",
        };
        var mockJson = JsonSerializer.Serialize(mockResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        var handler = new MockHttpHandler(mockJson, HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var client = new A2AClient(http);

        var executor = new DefaultA2ATaskExecutor(client);
        var node = new TaskNode
        {
            Id = "node-1",
            TaskType = "a2a/external",
            Description = "Delegate to remote agent",
            Metadata = new Dictionary<string, object?>
            {
                ["agentUrl"] = "http://localhost:9999/a2a",
            },
        };

        var result = await executor.ExecuteAsync(node);
        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.ShouldContain("Task completed successfully");
    }

    [Fact]
    public async Task DefaultA2ATaskExecutor_NoAgentUrl_ReturnsFailure()
    {
        var executor = new DefaultA2ATaskExecutor();
        var node = new TaskNode
        {
            Id = "node-2",
            TaskType = "a2a/external",
            Description = "Task without agent URL",
        };

        var result = await executor.ExecuteAsync(node);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task DefaultA2ATaskExecutor_RemoteFailure_ReturnsFailure()
    {
        var handler = new MockHttpHandler("{}", HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler);
        var client = new A2AClient(http);

        var executor = new DefaultA2ATaskExecutor(client);
        var node = new TaskNode
        {
            Id = "node-3",
            TaskType = "a2a/external",
            Description = "Remote agent returns error",
            Metadata = new Dictionary<string, object?>
            {
                ["agentUrl"] = "http://localhost:9999/a2a",
            },
        };

        var result = await executor.ExecuteAsync(node);
        result.Success.ShouldBeFalse();
    }

    // ── Mock HTTP handler for testing ─────────────────────────────

    private sealed class MockHttpHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}