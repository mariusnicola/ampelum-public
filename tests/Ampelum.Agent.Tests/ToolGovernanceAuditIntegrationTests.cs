using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

/// <summary>End-to-end coverage for the central tool governance path. The tests
/// assert audit evidence instead of trusting only the returned tool result.</summary>
public sealed class ToolGovernanceAuditIntegrationTests
{
    private static readonly AgentIdentity Caller = new("audit-test", "audit-session", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task AllowedInvocation_EmitsOrderedCorrelatedAuditWithoutRawArguments()
    {
        var manifest = Manifest("read", DangerTier.Safe);
        var (dispatcher, audit, _) = Create(manifest);
        dispatcher.RegisterHandler("read", (_, _) => Task.FromResult(Result(true, "done")));

        var result = await dispatcher.InvokeAsync("read", Args(("secret", "never-store-this")), Caller, new ToolInvocationContext("trace-1", "workflow-1"));

        result.Success.ShouldBeTrue();
        var entries = await audit.QueryAsync();
        entries.Select(x => x.EventType).ShouldBe(["policy.decision", "handler.succeeded", "invocation.completed"]);
        entries.ShouldAllBe(x => x.TraceId == "trace-1" && x.WorkflowId == "workflow-1");
        entries.ShouldAllBe(x => x.Sequence > 0);
        entries.Select(x => x.Sequence).ShouldBe(entries.Select(x => x.Sequence).OrderBy(x => x));
        foreach (var entry in entries) entry.Args["secret"].ShouldBe("[provided]");
    }

    [Fact]
    public async Task BlockedInvocation_IsAuditedAndNeverCallsHandler()
    {
        var (dispatcher, audit, _) = Create(Manifest("blocked", DangerTier.Catastrophic));
        var called = false;
        dispatcher.RegisterHandler("blocked", (_, _) => { called = true; return Task.FromResult(Result(true)); });

        var result = await dispatcher.InvokeAsync("blocked", Args(), Caller, new ToolInvocationContext("trace-b", "workflow-b"));

        result.Success.ShouldBeFalse();
        called.ShouldBeFalse();
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldBe(["policy.decision", "invocation.blocked"]);
    }

    [Fact]
    public async Task ConfirmationRequired_RecordsRequestThenApprovedLifecycleBeforeHandler()
    {
        var confirmations = new InMemoryToolConfirmationStore();
        var (dispatcher, audit, _) = Create(Manifest("delete", DangerTier.Safe, requiresConfirmation: true), confirmations: confirmations);
        var called = 0;
        dispatcher.RegisterHandler("delete", (_, _) => { called++; return Task.FromResult(Result(true)); });
        var context = new ToolInvocationContext("trace-c", "workflow-c");

        var waiting = await dispatcher.InvokeAsync("delete", Args(), Caller, context);
        waiting.Success.ShouldBeFalse();
        var confirmationId = (await audit.QueryAsync()).Single(x => x.EventType == "confirmation.requested").ConfirmationId;
        confirmationId.ShouldNotBeNullOrWhiteSpace();
        (await confirmations.DecideAsync(confirmationId!, true, "human-review")).ShouldNotBeNull();

        var approved = await dispatcher.InvokeAsync("delete", Args(), Caller, context with { ConfirmationId = confirmationId });

        approved.Success.ShouldBeTrue();
        called.ShouldBe(1);
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldContain("confirmation.approved");
    }

    [Fact]
    public async Task ExpiredAndRevokedObservationTokens_AreAuditedAndBlocked()
    {
        var store = new InMemoryTokenStore();
        store.NextCallIndex();
        store.Store(new ObservationToken("expired", "/target", "read", Caller.InstanceId, 0, 0));
        var edit = Manifest("edit", DangerTier.Destructive) with { RequiresObservationToken = new ObservationRequirement { ArgName = "token", ResourcePathArg = "path" } };
        var (dispatcher, audit, _) = Create(edit, store: store);
        dispatcher.RegisterHandler("edit", (_, _) => Task.FromResult(Result(true)));

        var expired = await dispatcher.InvokeAsync("edit", Args(("token", "expired"), ("path", "/target")), Caller, new ToolInvocationContext("trace-t", "workflow-t"));
        expired.Success.ShouldBeFalse();
        store.Store(new ObservationToken("revoked", "/target", "read", Caller.InstanceId, 1, 5));
        store.PurgeSession();
        var revoked = await dispatcher.InvokeAsync("edit", Args(("token", "revoked"), ("path", "/target")), Caller, new ToolInvocationContext("trace-t", "workflow-t"));

        revoked.Success.ShouldBeFalse();
        (await audit.QueryAsync()).Count(x => x.EventType == "observation.token.rejected").ShouldBe(2);
    }

    [Fact]
    public async Task ObservationTokenIssuanceAndUse_AreBothAudited()
    {
        var loader = new InMemoryToolLoader();
        loader.Register(Manifest("observe", DangerTier.Safe) with { IssuesObservationToken = new ObservationSource { TokenType = TokenType.ReadToken, ResourcePathArg = "path" } });
        loader.Register(Manifest("edit", DangerTier.Destructive) with { RequiresObservationToken = new ObservationRequirement { ArgName = "token", ResourcePathArg = "path" } });
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit);
        dispatcher.RegisterHandler("observe", (_, _) => Task.FromResult(Result(true, "observed")));
        dispatcher.RegisterHandler("edit", (_, _) => Task.FromResult(Result(true)));
        var correlation = new ToolInvocationContext("trace-o", "workflow-o");

        await dispatcher.InvokeAsync("observe", Args(("path", "/target")), Caller, correlation);
        var token = (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.issued").ObservationTokenId;
        var edited = await dispatcher.InvokeAsync("edit", Args(("path", "/target"), ("token", token!)), Caller, correlation);

        edited.Success.ShouldBeTrue();
        (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.consumed").ObservationTokenId.ShouldBe(token);
    }

    [Fact]
    public async Task ObservationToken_IsSurfacedOnToolResult_AndMatchesAuditTrail()
    {
        var loader = new InMemoryToolLoader();
        loader.Register(Manifest("observe", DangerTier.Safe) with { IssuesObservationToken = new ObservationSource { TokenType = TokenType.ReadToken, ResourcePathArg = "path" } });
        loader.Register(Manifest("edit", DangerTier.Destructive) with { RequiresObservationToken = new ObservationRequirement { ArgName = "token", ResourcePathArg = "path" } });
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit);
        dispatcher.RegisterHandler("observe", (_, _) => Task.FromResult(Result(true, "observed")));
        dispatcher.RegisterHandler("edit", (_, _) => Task.FromResult(Result(true)));
        var correlation = new ToolInvocationContext("trace-r", "workflow-r");

        var observed = await dispatcher.InvokeAsync("observe", Args(("path", "/target")), Caller, correlation);
        var issuedToken = (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.issued").ObservationTokenId;
        var edited = await dispatcher.InvokeAsync("edit", Args(("path", "/target"), ("token", issuedToken!)), Caller, correlation);
        var consumedToken = (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.consumed").ObservationTokenId;

        observed.ObservationTokenId.ShouldBe(issuedToken);
        edited.ObservationTokenId.ShouldBe(consumedToken);
        edited.ObservationTokenId.ShouldBe(issuedToken);
    }

    [Fact]
    public async Task HandlerFailureAndCancellation_AreBothRecorded()
    {
        var (dispatcher, audit, _) = Create(Manifest("work", DangerTier.Safe));
        dispatcher.RegisterHandler("work", (_, ct) => ct.IsCancellationRequested ? Task.FromCanceled<ToolResult>(ct) : throw new InvalidOperationException("handler broke"));

        var failed = await dispatcher.InvokeAsync("work", Args(), Caller, new ToolInvocationContext("trace-f", "workflow-f"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await dispatcher.InvokeAsync("work", Args(), Caller, new ToolInvocationContext("trace-f", "workflow-f"), cancellation.Token);

        failed.Success.ShouldBeFalse();
        cancelled.Success.ShouldBeFalse();
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldContain("handler.failed");
        (await audit.QueryAsync()).Select(x => x.EventType).ShouldContain("handler.cancelled");
    }

    [Fact]
    public async Task MissingHandler_DoesNotConsumeObservationToken_AndTokenRemainsUsable()
    {
        var loader = new InMemoryToolLoader();
        var manifest = Manifest("ghost", DangerTier.Destructive) with { RequiresObservationToken = new ObservationRequirement { ArgName = "token", ResourcePathArg = "path" } };
        loader.Register(manifest);
        var directory = Path.Combine(Path.GetTempPath(), $"ampelum-ghost-tokens-{Guid.NewGuid():N}");
        var tokenPath = Path.Combine(directory, "tokens.db");
        try
        {
            var tokenStore = new SqliteObservationTokenStore(tokenPath);
            var scope = new ObservationTokenScope("default", Caller.InstanceId, Caller.AgentType);
            var token = new ObservationToken("ghost-token", "/target", "read", Caller.InstanceId, 0, 100);
            tokenStore.Store(token, scope, DateTimeOffset.UtcNow.AddMinutes(5), new ObservationTokenAuditContext("trace-g", "workflow-g", Caller.InstanceId));
            var audit = new InMemoryToolAuditLog();
            var dispatcher = new ToolDispatcher(loader, tokenStore, new PolicyEngine(), Caller, auditLog: audit);

            var missing = await dispatcher.InvokeAsync("ghost", Args(("token", "ghost-token"), ("path", "/target")), Caller, new ToolInvocationContext("trace-g", "workflow-g"));

            missing.Success.ShouldBeFalse();
            missing.Error!.ShouldContain("No handler registered");
            (await audit.QueryAsync()).Select(x => x.EventType).ShouldNotContain("observation.token.consumed");

            dispatcher.RegisterHandler("ghost", (_, _) => Task.FromResult(Result(true)));
            var succeeded = await dispatcher.InvokeAsync("ghost", Args(("token", "ghost-token"), ("path", "/target")), Caller, new ToolInvocationContext("trace-g2", "workflow-g2"));

            succeeded.Success.ShouldBeTrue();
            succeeded.ObservationTokenId.ShouldBe("ghost-token");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandlerThrowsAfterTokenConsumed_ResultStillSurfacesConsumedTokenId()
    {
        var loader = new InMemoryToolLoader();
        loader.Register(Manifest("observe", DangerTier.Safe) with { IssuesObservationToken = new ObservationSource { TokenType = TokenType.ReadToken, ResourcePathArg = "path" } });
        var manifest = Manifest("edit", DangerTier.Destructive) with { RequiresObservationToken = new ObservationRequirement { ArgName = "token", ResourcePathArg = "path" } };
        loader.Register(manifest);
        var audit = new InMemoryToolAuditLog();
        var dispatcher = new ToolDispatcher(loader, new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit);
        dispatcher.RegisterHandler("observe", (_, _) => Task.FromResult(Result(true, "observed")));
        dispatcher.RegisterHandler("edit", (_, _) => throw new InvalidOperationException("handler broke after token consumed"));
        var correlation = new ToolInvocationContext("trace-x", "workflow-x");

        await dispatcher.InvokeAsync("observe", Args(("path", "/target")), Caller, correlation);
        var issuedToken = (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.issued").ObservationTokenId;
        var edited = await dispatcher.InvokeAsync("edit", Args(("path", "/target"), ("token", issuedToken!)), Caller, correlation);
        var consumedEntry = (await audit.QueryAsync()).Single(x => x.EventType == "observation.token.consumed");

        edited.Success.ShouldBeFalse();
        edited.ObservationTokenId.ShouldNotBeNull();
        edited.ObservationTokenId.ShouldBe(consumedEntry.ObservationTokenId);
        edited.ObservationTokenId.ShouldBe(issuedToken);
    }

    [Fact]
    public async Task SqliteAuditLog_SurvivesNewLogInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ampelum-audit-{Guid.NewGuid():N}.db");
        try
        {
            var durable = new SqliteToolAuditLog(path);
            var (dispatcher, _, _) = Create(Manifest("read", DangerTier.Safe), audit: durable);
            dispatcher.RegisterHandler("read", (_, _) => Task.FromResult(Result(true)));
            await dispatcher.InvokeAsync("read", Args(("payload", "raw-value")), Caller, new ToolInvocationContext("trace-sql", "workflow-sql"));

            var restored = await new SqliteToolAuditLog(path).QueryAsync();
            restored.Count.ShouldBe(3);
            restored.ShouldAllBe(x => x.TraceId == "trace-sql" && x.WorkflowId == "workflow-sql");
            foreach (var entry in restored) entry.Args["payload"].ShouldBe("[provided]");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SqliteAuditLog_RoundTripsObservationTokenIdOnResult()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ampelum-audit-token-{Guid.NewGuid():N}.db");
        try
        {
            var durable = new SqliteToolAuditLog(path);
            var entry = new ToolAuditEntry(
                Guid.NewGuid().ToString("N"),
                "edit",
                Caller.InstanceId,
                Caller.AgentType,
                new Dictionary<string, object?> { ["path"] = "[provided]" },
                PolicyDecision.Allowed,
                new ToolResult("edit", true, "done", null, DateTimeOffset.UtcNow, TimeSpan.Zero, "token-abc"),
                DateTimeOffset.UtcNow,
                "trace-token",
                "workflow-token",
                "handler.succeeded");
            await durable.RecordAsync(entry, TestContext.Current.CancellationToken);

            var restored = await new SqliteToolAuditLog(path).QueryAsync();

            var restoredEntry = restored.Single();
            restoredEntry.Result.ShouldNotBeNull();
            restoredEntry.Result!.ObservationTokenId.ShouldBe("token-abc");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static (ToolDispatcher Dispatcher, InMemoryToolAuditLog Audit, IToolConfirmationStore Confirmations) Create(ToolManifest manifest, InMemoryTokenStore? store = null, IToolConfirmationStore? confirmations = null, IToolAuditLog? audit = null)
    {
        var loader = new InMemoryToolLoader();
        loader.Register(manifest);
        var memoryAudit = audit as InMemoryToolAuditLog ?? new InMemoryToolAuditLog();
        var confirmationStore = confirmations ?? new InMemoryToolConfirmationStore();
        return (new ToolDispatcher(loader, store ?? new InMemoryTokenStore(), new PolicyEngine(), Caller, auditLog: audit ?? memoryAudit, confirmationStore: confirmationStore), memoryAudit, confirmationStore);
    }

    private static ToolManifest Manifest(string name, DangerTier tier, bool requiresConfirmation = false) => new() { Name = name, Description = name, DangerTier = tier, Reversibility = Reversibility.Reversible, RequiresConfirmation = requiresConfirmation };
    private static ToolResult Result(bool success, string? output = null) => new(success, output, success ? null : "failure", PolicyDecision.Allowed);
    private static IReadOnlyDictionary<string, JsonElement> Args(params (string Name, string Value)[] values) => values.ToDictionary(x => x.Name, x => JsonSerializer.SerializeToElement(x.Value));
}
