using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Agent.A2A;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class A2ATests
{
    // ── TaskState enum ─────────────────────────────────────────────

    [Fact]
    public void TaskState_Submitted_HasValueZero()
    {
        ((int)TaskState.Submitted).ShouldBe(0);
    }

    [Fact]
    public void TaskState_Working_HasValueOne()
    {
        ((int)TaskState.Working).ShouldBe(1);
    }

    [Fact]
    public void TaskState_Completed_HasValueThree()
    {
        ((int)TaskState.Completed).ShouldBe(3);
    }

    [Fact]
    public void TaskState_Canceled_HasValueFour()
    {
        ((int)TaskState.Canceled).ShouldBe(4);
    }

    [Fact]
    public void TaskState_Failed_HasValueFive()
    {
        ((int)TaskState.Failed).ShouldBe(5);
    }

    // ── A2ATask record ─────────────────────────────────────────────

    [Fact]
    public void A2ATask_CanBeCreated()
    {
        var task = new A2ATask
        {
            Id = "task-1",
            State = TaskState.Submitted,
            Messages = new MessageCollection([]),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        task.Id.ShouldBe("task-1");
        task.State.ShouldBe(TaskState.Submitted);
    }

    [Fact]
    public void A2ATask_WithArtifacts_IncludesParts()
    {
        var task = new A2ATask
        {
            Id = "task-2",
            State = TaskState.Completed,
            Messages = new MessageCollection([]),
            Artifacts = new List<A2AArtifact>
            {
                new()
                {
                    Index = "0",
                    Parts = new A2APart { Text = "hello" },
                },
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        task.Artifacts.ShouldNotBeNull();
        task.Artifacts.Count.ShouldBe(1);
        task.Artifacts[0].Parts.Text.ShouldBe("hello");
    }

    // ── A2AMessage types ───────────────────────────────────────────

    [Fact]
    public void A2AMessage_CanBeCreated_WithTextPart()
    {
        var msg = new A2AMessage
        {
            Id = "msg-1",
            Role = MessageRole.User,
            Parts = new List<A2APart>
            {
                new() { Text = "Hello, agent!" },
            },
        };

        msg.Role.ShouldBe(MessageRole.User);
        msg.Parts[0].Text.ShouldBe("Hello, agent!");
    }

    [Fact]
    public void A2AMessage_CanBeCreated_WithFilePart()
    {
        var msg = new A2AMessage
        {
            Id = "msg-2",
            Role = MessageRole.Agent,
            Parts = new List<A2APart>
            {
                new()
                {
                    File = new A2AFileContent
                    {
                        Name = "report.pdf",
                        MimeType = "application/pdf",
                        Bytes = "dGVzdA==",
                    },
                },
            },
        };

        var file = msg.Parts[0].File;
        file.ShouldNotBeNull();
        file!.Name.ShouldBe("report.pdf");
        file!.MimeType.ShouldBe("application/pdf");
        file!.Bytes.ShouldBe("dGVzdA==");
    }

    [Fact]
    public void A2AMessage_CanBeCreated_WithDataPart()
    {
        var msg = new A2AMessage
        {
            Id = "msg-3",
            Role = MessageRole.Agent,
            Parts = new List<A2APart>
            {
                new()
                {
                    Data = new A2ADataContent
                    {
                        Data = new Dictionary<string, object?>
                        {
                            ["key"] = "value",
                            ["count"] = 42,
                        },
                    },
                },
            },
        };

        var data = msg.Parts[0].Data;
        data.ShouldNotBeNull();
        data!.Data["key"].ShouldBe("value");
    }

    [Fact]
    public void MessageRole_User_HasValueZero()
    {
        ((int)MessageRole.User).ShouldBe(0);
    }

    [Fact]
    public void MessageRole_Agent_HasValueOne()
    {
        ((int)MessageRole.Agent).ShouldBe(1);
    }

    // ── A2AAgentCard ───────────────────────────────────────────────

    [Fact]
    public void A2AAgentCard_CanBeCreated()
    {
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com/agent",
            Provider = "Ampelum",
            Skills = new List<A2AAgentSkill>
            {
                new()
                {
                    Id = "skill-1",
                    Name = "Code Review",
                    Description = "Reviews pull requests",
                    Tags = new[] { "code", "review" },
                },
            },
            Capabilities = new List<A2AAgentCapability>
            {
                new() { Streaming = true, LongRunningTasks = false },
            },
        };

        card.Name.ShouldBe("Test Agent");
        card.Skills.ShouldNotBeNull();
        card.Skills.Count.ShouldBe(1);
        card.Capabilities.ShouldNotBeNull();
        card.Capabilities[0].Streaming.ShouldBeTrue();
    }

    [Fact]
    public void A2AAgentCard_AuthenticationSchemes_CanBeSet()
    {
        var card = new A2AAgentCard
        {
            Name = "Secure Agent",
            Description = "An agent with auth",
            Version = "2.0.0",
            Url = "https://secure.example.com",
            AuthenticationSchemes = new List<A2AAuthenticationScheme>
            {
                new() { Scheme = "bearer" },
                new() { Scheme = "oauth2", Credentials = "client-id" },
            },
        };

        card.AuthenticationSchemes.ShouldNotBeNull();
        card.AuthenticationSchemes.Count.ShouldBe(2);
        card.AuthenticationSchemes[1].Scheme.ShouldBe("oauth2");
    }

    // ── A2ARequest / A2AResponse ───────────────────────────────────

    [Fact]
    public void A2ARequest_Defaults_To_Jsonrpc_2_0()
    {
        var req = new A2ARequest
        {
            Method = A2AMethods.SendMessage,
            Params = new A2ARequestParams(),
        };

        req.Jsonrpc.ShouldBe("2.0");
    }

    [Fact]
    public void A2AResponse_Defaults_To_Jsonrpc_2_0()
    {
        var resp = new A2AResponse();

        resp.Jsonrpc.ShouldBe("2.0");
    }

    [Fact]
    public void A2AResponse_Can_Carry_Error()
    {
        var resp = new A2AResponse
        {
            Error = new A2AError
            {
                Code = -32601,
                Message = "Method not found",
            },
        };

        resp.Error.ShouldNotBeNull();
        resp.Error.Code.ShouldBe(-32601);
    }

    [Fact]
    public void A2AResponse_Can_Carry_Result_With_Task()
    {
        var resp = new A2AResponse
        {
            Result = new A2AResult
            {
                Task = new A2ATask
                {
                    Id = "task-r1",
                    State = TaskState.Completed,
                    Messages = new MessageCollection([]),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            },
        };

        resp.Result.ShouldNotBeNull();
        resp.Result.Task.ShouldNotBeNull();
        resp.Result.Task.Id.ShouldBe("task-r1");
    }

    // ── A2AMethods constants ──────────────────────────────────────

    [Fact]
    public void A2AMethods_HasCorrectConstants()
    {
        A2AMethods.SendMessage.ShouldBe("tasks/send");
        A2AMethods.SendStreamingMessage.ShouldBe("tasks/sendSubscribe");
        A2AMethods.GetTask.ShouldBe("tasks/get");
        A2AMethods.ListTasks.ShouldBe("tasks/list");
        A2AMethods.CancelTask.ShouldBe("tasks/cancel");
        A2AMethods.AgentCardPath.ShouldBe("/.well-known/agent-card");
    }

    // ── MessageCollection ──────────────────────────────────────────

    [Fact]
    public void MessageCollection_CanBeEnumerated()
    {
        var msgs = new List<A2AMessage>
        {
            new() { Id = "m1", Role = MessageRole.User, Parts = [] },
            new() { Id = "m2", Role = MessageRole.Agent, Parts = [] },
        };

        var coll = new MessageCollection(msgs);

        coll.Count.ShouldBe(2);
        coll[0].Id.ShouldBe("m1");
        coll[1].Id.ShouldBe("m2");
    }

    // ── A2AFailureMode ─────────────────────────────────────────────

    [Fact]
    public void A2AFailureMode_HasExpectedValues()
    {
        ((int)A2AFailureMode.Unreachable).ShouldBe(0);
        ((int)A2AFailureMode.Partial).ShouldBe(1);
        ((int)A2AFailureMode.TimedOut).ShouldBe(2);
        ((int)A2AFailureMode.SchemaMismatch).ShouldBe(3);
        ((int)A2AFailureMode.IdentityFailure).ShouldBe(4);
    }

    // ── Default implementations ────────────────────────────────────

    [Fact]
    public async Task DefaultAgentCardVerifier_NowVerifiesJwt()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("agent", new[] { "read" }, TimeSpan.FromHours(1));

        var identity = await verifier.VerifyAsync(jwt.Token);
        identity.ShouldNotBeNull();
    }

    [Fact]
    public void DefaultA2ATaskExecutor_HasCorrectTaskType()
    {
        var executor = new DefaultA2ATaskExecutor();
        executor.TaskType.ShouldBe("a2a/external");
    }

    // ── CapabilityWarning ──────────────────────────────────────────

    [Fact]
    public void CapabilityWarning_CanBeCreated()
    {
        var warning = new CapabilityWarning("read", "Capability not recognized");

        warning.CapabilityName.ShouldBe("read");
        warning.Warning.ShouldBe("Capability not recognized");
    }

    // ── TrustDecision enum ─────────────────────────────────────────

    [Fact]
    public void TrustDecision_Allow_HasValueZero()
    {
        ((int)TrustDecision.Allow).ShouldBe(0);
    }

    [Fact]
    public void TrustDecision_RequireConfirmation_HasValueOne()
    {
        ((int)TrustDecision.RequireConfirmation).ShouldBe(1);
    }

    [Fact]
    public void TrustDecision_Block_HasValueTwo()
    {
        ((int)TrustDecision.Block).ShouldBe(2);
    }

    // ── DefaultA2ATrustPolicy ──────────────────────────────────────

    [Fact]
    public void DefaultA2ATrustPolicy_NullVerifiedIdentity_Blocks()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };

        var decision = policy.EvaluateDelegation(card, verifiedIdentity: null, TrustLevel.Provisional);

        decision.ShouldBe(TrustDecision.Block);
    }

    [Fact]
    public void DefaultA2ATrustPolicy_TrustLevelNone_Blocks()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };
        var identity = new AgentIdentity(
            AgentType: "test-agent",
            InstanceId: Guid.NewGuid().ToString(),
            Scope: new[] { "read" },
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var decision = policy.EvaluateDelegation(card, identity, TrustLevel.None);

        decision.ShouldBe(TrustDecision.Block);
    }

    [Fact]
    public void DefaultA2ATrustPolicy_TrustLevelUnknown_RequiresConfirmation()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };
        var identity = new AgentIdentity(
            AgentType: "test-agent",
            InstanceId: Guid.NewGuid().ToString(),
            Scope: new[] { "read" },
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var decision = policy.EvaluateDelegation(card, identity, TrustLevel.Unknown);

        decision.ShouldBe(TrustDecision.RequireConfirmation);
    }

    [Fact]
    public void DefaultA2ATrustPolicy_TrustLevelProvisional_Allows()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };
        var identity = new AgentIdentity(
            AgentType: "test-agent",
            InstanceId: Guid.NewGuid().ToString(),
            Scope: new[] { "read" },
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var decision = policy.EvaluateDelegation(card, identity, TrustLevel.Provisional);

        decision.ShouldBe(TrustDecision.Allow);
    }

    [Fact]
    public void DefaultA2ATrustPolicy_TrustLevelEstablished_Allows()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };
        var identity = new AgentIdentity(
            AgentType: "test-agent",
            InstanceId: Guid.NewGuid().ToString(),
            Scope: new[] { "read" },
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var decision = policy.EvaluateDelegation(card, identity, TrustLevel.Established);

        decision.ShouldBe(TrustDecision.Allow);
    }

    [Fact]
    public void DefaultA2ATrustPolicy_TrustLevelTrusted_Allows()
    {
        var policy = new DefaultA2ATrustPolicy();
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
        };
        var identity = new AgentIdentity(
            AgentType: "test-agent",
            InstanceId: Guid.NewGuid().ToString(),
            Scope: new[] { "read" },
            IssuedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1)
        );

        var decision = policy.EvaluateDelegation(card, identity, TrustLevel.Trusted);

        decision.ShouldBe(TrustDecision.Allow);
    }

    // ── DefaultAgentCardVerifier new methods ───────────────────────

    [Fact]
    public async Task DefaultAgentCardVerifier_VerifyIdentityAsync_NullJwt_ReturnsNull()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            AuthenticationSchemes = new List<A2AAuthenticationScheme>
            {
                new() { Scheme = "bearer", Credentials = null },
            },
        };

        var identity = await verifier.VerifyIdentityAsync(card);

        identity.ShouldBeNull();
    }

    [Fact]
    public async Task DefaultAgentCardVerifier_VerifyIdentityAsync_NoBearerScheme_ReturnsNull()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            AuthenticationSchemes = new List<A2AAuthenticationScheme>
            {
                new() { Scheme = "oauth2", Credentials = "some-token" },
            },
        };

        var identity = await verifier.VerifyIdentityAsync(card);

        identity.ShouldBeNull();
    }

    [Fact]
    public async Task DefaultAgentCardVerifier_VerifyIdentityAsync_ValidJwt_ReturnsIdentity()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("test-agent", new[] { "read" }, TimeSpan.FromHours(1));

        var card = new A2AAgentCard
        {
            Name = "test-agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            AuthenticationSchemes = new List<A2AAuthenticationScheme>
            {
                new() { Scheme = "bearer", Credentials = jwt.Token },
            },
        };

        var identity = await verifier.VerifyIdentityAsync(card);

        identity.ShouldNotBeNull();
        identity!.AgentType.ShouldBe("test-agent");
    }

    [Fact]
    public void DefaultAgentCardVerifier_VerifyCapabilities_NoCapabilities_ReturnsNoWarnings()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            Capabilities = null,
        };

        var warnings = verifier.VerifyCapabilities(card);

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void DefaultAgentCardVerifier_VerifyCapabilities_EmptyCapabilities_ReturnsNoWarnings()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            Capabilities = new List<A2AAgentCapability>(),
        };

        var warnings = verifier.VerifyCapabilities(card);

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void DefaultAgentCardVerifier_VerifyCapabilities_ValidCapabilities_ReturnsNoWarnings()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var card = new A2AAgentCard
        {
            Name = "Test Agent",
            Description = "A test agent",
            Version = "1.0.0",
            Url = "https://example.com",
            Capabilities = new List<A2AAgentCapability>
            {
                new() { Streaming = true, LongRunningTasks = true },
                new() { Streaming = false, PushNotifications = true },
            },
        };

        var warnings = verifier.VerifyCapabilities(card);

        warnings.ShouldBeEmpty();
    }
}