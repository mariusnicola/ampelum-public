using System.Text.Json;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Mcp;

/// <summary>End-to-end coverage proving the MCP layer never bypasses tool governance.
/// Mirrors <c>ToolGovernanceAuditIntegrationTests</c>'s pattern (assert audit evidence,
/// not just the returned result) but drives the call through
/// <see cref="McpServerAdapter.HandleToolsCallAsync"/> instead of the dispatcher directly,
/// so a regression that lets the Mcp layer skip <c>IToolDispatcher</c> would be caught here.</summary>
public sealed class McpGovernanceIntegrationTests
{
    private static readonly AgentIdentity Caller = new("mcp-test", "mcp-session", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task CatastrophicTool_CalledViaMcpAdapter_IsDeniedAndNeverReachesHandler()
    {
        var loader = new InMemoryToolLoader();
        loader.Register(Manifest("wipe_database", DangerTier.Catastrophic));
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit);
        var handlerCalled = false;
        dispatcher.RegisterHandler("wipe_database", (_, _) => { handlerCalled = true; return Task.FromResult(new ToolResult(true, "done", null, PolicyDecision.Allowed)); });
        var adapter = new McpServerAdapter(loader, dispatcher, new McpServerOptions("test-server"));

        var result = await adapter.HandleToolsCallAsync(
            "wipe_database",
            new Dictionary<string, object?> { { "confirm", true } },
            Caller,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        handlerCalled.ShouldBeFalse();
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldBe(["policy.decision", "invocation.blocked"]);
    }

    [Fact]
    public async Task ConfirmationRequiredTool_CalledViaMcpAdapter_PausesUntilApproved()
    {
        var loader = new InMemoryToolLoader();
        loader.Register(Manifest("delete_record", DangerTier.Safe, requiresConfirmation: true));
        var confirmations = new InMemoryToolConfirmationStore();
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit, confirmationStore: confirmations);
        var handlerCalls = 0;
        dispatcher.RegisterHandler("delete_record", (_, _) => { handlerCalls++; return Task.FromResult(new ToolResult(true, "deleted", null, PolicyDecision.Allowed)); });
        var adapter = new McpServerAdapter(loader, dispatcher, new McpServerOptions("test-server"));

        var waiting = await adapter.HandleToolsCallAsync("delete_record", new Dictionary<string, object?>(), Caller, TestContext.Current.CancellationToken);

        waiting.Success.ShouldBeFalse();
        handlerCalls.ShouldBe(0);
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldContain("confirmation.requested");
    }

    // ── Live tools/list wiring (McpToolSource + IMcpTransport) ─────────

    [Fact]
    public async Task SurveyAsync_WithLiveTransport_AndVerifier_TrustsServerDeclaredTier()
    {
        // A verifier is present, so the server's self-declared tier is trusted for tools
        // the verifier accepts.
        var transport = new FakeMcpTransport([
            new McpToolDefinition("remote_read", "Reads a remote resource", nameof(DangerTier.Safe), RequiresConfirmation: false)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport, new AllowingVerifier());

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.Count.ShouldBe(1);
        surveys[0].Name.ShouldBe("remote_read");
        surveys[0].DangerTier.ShouldBe(DangerTier.Safe);
    }

    [Fact]
    public async Task SurveyAsync_WithLiveTransport_NoVerifier_SelfDeclaredSafeIsCappedToCatastrophic()
    {
        // No verifier configured: an untrusted/unverified remote server can self-declare
        // "Safe" with requiresConfirmation:false, but that declaration must not be trusted at
        // face value. Every discovered tool is capped to Catastrophic regardless of what the
        // server claims.
        var transport = new FakeMcpTransport([
            new McpToolDefinition("remote_read", "Reads a remote resource", nameof(DangerTier.Safe), RequiresConfirmation: false)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport);

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.Count.ShouldBe(1);
        surveys[0].Name.ShouldBe("remote_read");
        surveys[0].DangerTier.ShouldBe(DangerTier.Catastrophic);
        surveys[0].RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task SurveyAsync_WithLiveTransport_NoDangerTierAnnotation_DefaultsToCatastrophicNotDestructive()
    {
        // Simulates HttpMcpTransport's real wire behavior for a tool with no _meta.dangerTier
        // annotation at all: the transport now leaves the value as the empty-string sentinel
        // (it no longer silently normalises to "Destructive"), so McpToolSource's own
        // Catastrophic fail-safe is the one and only place this decision gets made.
        var transport = new FakeMcpTransport([
            new McpToolDefinition("unannotated_tool", "No _meta.dangerTier at all", DangerTier: string.Empty, RequiresConfirmation: false)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport, new AllowingVerifier());

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.Count.ShouldBe(1);
        surveys[0].DangerTier.ShouldBe(DangerTier.Catastrophic);
    }

    [Fact]
    public async Task SurveyAsync_WithLiveTransport_UnclassifiedTool_DefaultsToCatastrophicAndIsBlockable()
    {
        // An unparseable _meta.dangerTier string from the server also fails safe to
        // Catastrophic (verifier present so the cap-when-unverified path isn't what's under
        // test here — the parse-failure fallback in McpToolSource.ToManifest is).
        var transport = new FakeMcpTransport([
            new McpToolDefinition("unknown_tool", "No governance annotation", DangerTier: "not-a-real-tier", RequiresConfirmation: false)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport, new AllowingVerifier());

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.Count.ShouldBe(1);
        surveys[0].DangerTier.ShouldBe(DangerTier.Catastrophic);

        // And feeding that discovered manifest through the same governed dispatcher path
        // denies it, exactly like any other Catastrophic-tier tool.
        var manifest = await source.TryUnpackAsync("unknown_tool", TestContext.Current.CancellationToken);
        manifest.ShouldNotBeNull();
        var loader = new InMemoryToolLoader();
        loader.Register(manifest!);
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit);
        var called = false;
        dispatcher.RegisterHandler("unknown_tool", (_, _) => { called = true; return Task.FromResult(new ToolResult(true, null, null, PolicyDecision.Allowed)); });

        var result = await dispatcher.InvokeAsync("unknown_tool", new Dictionary<string, JsonElement>(), Caller, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        called.ShouldBeFalse();
    }

    [Fact]
    public async Task SurveyAsync_RegisteredManifest_TakesPrecedenceOverDiscoveredToolOfSameName()
    {
        var transport = new FakeMcpTransport([
            new McpToolDefinition("dup_tool", "From transport", nameof(DangerTier.Catastrophic), RequiresConfirmation: true)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport);
        source.RegisterManifest(new ToolManifest
        {
            Name = "dup_tool",
            Description = "From manual registration",
            DangerTier = DangerTier.Safe,
            Reversibility = Reversibility.Reversible,
            RequiresConfirmation = false
        });

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.Count.ShouldBe(1);
        surveys[0].Description.ShouldBe("From manual registration");
        surveys[0].DangerTier.ShouldBe(DangerTier.Safe);
    }

    [Fact]
    public async Task SurveyAsync_DiscoveredTool_RejectedByVerifier_IsExcluded()
    {
        var transport = new FakeMcpTransport([
            new McpToolDefinition("untrusted_tool", "Fails verification", nameof(DangerTier.Safe), RequiresConfirmation: false)
        ]);
        var connection = new McpServerConnection("https://mcp.example.com", TrustLevel.Trusted);
        var source = new McpToolSource(connection, transport, new RejectingVerifier());

        var surveys = await source.SurveyAsync(TestContext.Current.CancellationToken);

        surveys.ShouldBeEmpty();
        (await source.TryUnpackAsync("untrusted_tool", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // ── Fakes ───────────────────────────────────────────────────────

    private sealed class FakeMcpTransport(IReadOnlyList<McpToolDefinition> tools) : IMcpTransport
    {
        public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken ct = default)
            => Task.FromResult(tools);

        public Task<McpToolsCallResponse> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
            => Task.FromResult(new McpToolsCallResponse(Success: true, Result: "ok"));
    }

    private sealed class RejectingVerifier : IToolManifestVerifier
    {
        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix)
            => new(Trusted: false, AssignedTier: TrustLevel.None, Reason: "rejected for test");
    }

    private sealed class AllowingVerifier : IToolManifestVerifier
    {
        public VerificationResult Verify(ToolManifest manifest, string sourcePrefix)
            => new(Trusted: true, AssignedTier: TrustLevel.Trusted, Reason: "allowed for test");
    }

    private static ToolManifest Manifest(string name, DangerTier tier, bool requiresConfirmation = false) => new()
    {
        Name = name,
        Description = name,
        DangerTier = tier,
        Reversibility = Reversibility.Reversible,
        RequiresConfirmation = requiresConfirmation
    };
}
