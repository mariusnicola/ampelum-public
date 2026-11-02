using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Tools;

public sealed class SqliteObservationTokenStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ampelum-observation-tokens", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_RecreatedStore_ConsumesOnce_AndWritesCorrelatedAudit()
    {
        var path = DatabasePath();
        var scope = Scope();
        var issueContext = Context("trace-issue", "workflow-1");
        var token = Token("restart-token");
        new SqliteObservationTokenStore(path).Store(token, scope, DateTimeOffset.UtcNow.AddMinutes(1), issueContext);

        var restarted = new SqliteObservationTokenStore(path);
        restarted.Consume(token.TokenId, scope, token.ResourcePath, Context("trace-consume", "workflow-1")).Consumed.ShouldBeTrue();
        restarted.Consume(token.TokenId, scope, token.ResourcePath, Context("trace-replay", "workflow-1")).Consumed.ShouldBeFalse();

        var audit = await restarted.QueryAuditAsync(token.TokenId, TestContext.Current.CancellationToken);
        audit.Select(x => x.EventType).ShouldBe(["issued", "consumed", "rejected"]);
        audit[1].Context.TraceId.ShouldBe("trace-consume");
        audit[1].Context.WorkflowId.ShouldBe("workflow-1");
    }

    [Fact]
    public async Task Consume_TwoStoreInstances_OnlyOneCanSucceed()
    {
        var path = DatabasePath();
        var scope = Scope();
        var token = Token("concurrent-token");
        new SqliteObservationTokenStore(path).Store(token, scope, DateTimeOffset.UtcNow.AddMinutes(1), Context());
        var first = new SqliteObservationTokenStore(path);
        var second = new SqliteObservationTokenStore(path);

        var results = await Task.WhenAll(
            Task.Run(() => first.Consume(token.TokenId, scope, token.ResourcePath, Context("trace-first", "workflow-concurrent")), TestContext.Current.CancellationToken),
            Task.Run(() => second.Consume(token.TokenId, scope, token.ResourcePath, Context("trace-second", "workflow-concurrent")), TestContext.Current.CancellationToken));

        results.Count(x => x.Consumed).ShouldBe(1);
        results.Count(x => !x.Consumed).ShouldBe(1);
    }

    [Theory]
    [InlineData("workspace", "other-workspace", "session-1", "issuer-1", "different workspace")]
    [InlineData("workspace", "workspace", "other-session", "issuer-1", "different session")]
    [InlineData("workspace", "workspace", "session-1", "other-issuer", "different issuer")]
    public void Consume_CrossAuthorityBinding_IsRejected(string workspace, string attemptedWorkspace, string attemptedSession, string attemptedIssuer, string reason)
    {
        var store = new SqliteObservationTokenStore(DatabasePath());
        var token = Token("bound-token");
        store.Store(token, new ObservationTokenScope(workspace, "session-1", "issuer-1"), DateTimeOffset.UtcNow.AddMinutes(1), Context());

        var result = store.Consume(token.TokenId, new ObservationTokenScope(attemptedWorkspace, attemptedSession, attemptedIssuer), token.ResourcePath, Context());

        result.Consumed.ShouldBeFalse();
        result.RejectionReason!.ShouldContain(reason);
    }

    [Fact]
    public async Task Consume_ExpiredToken_IsRejectedAndAudited()
    {
        var store = new SqliteObservationTokenStore(DatabasePath());
        var token = Token("expired-token");
        store.Store(token, Scope(), DateTimeOffset.UtcNow.AddMilliseconds(20), Context());
        await Task.Delay(60, TestContext.Current.CancellationToken);

        var result = store.Consume(token.TokenId, Scope(), token.ResourcePath, Context("trace-expired", "workflow-expired"));

        result.Consumed.ShouldBeFalse();
        result.RejectionReason!.ShouldContain("expired");
        (await store.QueryAuditAsync(token.TokenId, TestContext.Current.CancellationToken)).Select(x => x.EventType).ShouldBe(["issued", "rejected"]);
    }

    [Fact]
    public async Task Revoke_PreventsConsumption_AndWritesAudit()
    {
        var store = new SqliteObservationTokenStore(DatabasePath());
        var token = Token("revoked-token");
        var scope = Scope();
        store.Store(token, scope, DateTimeOffset.UtcNow.AddMinutes(1), Context());

        store.Revoke(token.TokenId, scope, "operator revoked observation", Context("trace-revoke", "workflow-revoke")).ShouldBeTrue();
        var result = store.Consume(token.TokenId, scope, token.ResourcePath, Context("trace-consume", "workflow-revoke"));

        result.Consumed.ShouldBeFalse();
        result.RejectionReason!.ShouldContain("revoked");
        var audit = await store.QueryAuditAsync(token.TokenId, TestContext.Current.CancellationToken);
        audit.Select(x => x.EventType).ShouldBe(["issued", "revoked", "rejected"]);
        audit[1].Reason.ShouldBe("operator revoked observation");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string DatabasePath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "tokens.db");
    }

    private static ObservationToken Token(string id) => new(id, "/resource/a", "read_resource", "session-1", 1, 10);
    private static ObservationTokenScope Scope() => new("workspace", "session-1", "issuer-1");
    private static ObservationTokenAuditContext Context(string trace = "trace-1", string workflow = "workflow-1") => new(trace, workflow, "actor-1");
}
