using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests.Identity;

public sealed class DefaultAgentIdentityProviderTests
{
    [Fact]
    public void Constructor_NullKey_Throws() =>
        Should.Throw<ArgumentException>(() => new DefaultAgentIdentityProvider(null!));

    [Fact]
    public void Constructor_ShortKey_Throws() =>
        Should.Throw<ArgumentException>(() => new DefaultAgentIdentityProvider(new byte[31]));

    [Fact]
    public async Task VerifyAsync_RejectsTokenSignedWithDifferentKey()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var other = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var jwt = await issuer.IssueAsync("a", new[] { "s" }, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        (await other.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task IssueAsync_CreatesValidIdentity()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var agentType = "test-agent";
        var scope = new[] { "tier1", "tier2" };
        var runDuration = TimeSpan.FromHours(1);

        var jwt = await provider.IssueAsync(agentType, scope, runDuration, TestContext.Current.CancellationToken);

        jwt.Token.ShouldNotBeNullOrEmpty();
        jwt.Claims.AgentType.ShouldBe(agentType);
        jwt.Claims.InstanceId.ShouldNotBeNullOrEmpty();
        jwt.Claims.Scope.ShouldBe(scope);
        jwt.Claims.ResumedFrom.ShouldBeNull();
        jwt.Claims.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InstanceId_IsUniquePerIssue()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());

        var jwt1 = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var jwt2 = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt1.Claims.InstanceId.ShouldNotBe(jwt2.Claims.InstanceId);
    }

    [Fact]
    public async Task AgentType_IsStableAcrossIssues()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var agentType = "ampelum";

        var jwt1 = await provider.IssueAsync(agentType, [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var jwt2 = await provider.IssueAsync(agentType, [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt1.Claims.AgentType.ShouldBe(agentType);
        jwt2.Claims.AgentType.ShouldBe(agentType);
    }

    [Fact]
    public async Task VerifyAsync_ValidatesSignature()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var verified = await provider.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken);

        verified.ShouldNotBeNull();
        verified!.InstanceId.ShouldBe(jwt.Claims.InstanceId);
        verified.AgentType.ShouldBe(jwt.Claims.AgentType);
    }

    [Fact]
    public async Task VerifyAsync_RejectsInvalidSignature()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        // Corrupt the signature
        var parts = jwt.Token.Split('.');
        var corrupted = $"{parts[0]}.{parts[1]}.corrupted";

        var verified = await provider.VerifyAsync(corrupted, TestContext.Current.CancellationToken);

        verified.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsExpiredToken()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);

        await Task.Delay(100);

        var verified = await provider.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken);

        verified.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsNullOrEmpty()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());

        var verified1 = await provider.VerifyAsync(string.Empty, TestContext.Current.CancellationToken);
        var verified2 = await provider.VerifyAsync("", TestContext.Current.CancellationToken);

        verified1.ShouldBeNull();
        verified2.ShouldBeNull();
    }

    [Fact]
    public async Task RenewAsync_ExtendsDuration()
    {
        var provider = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var jwt = await provider.IssueAsync("agent", ["scope1"], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var originalExpiry = jwt.Claims.ExpiresAt;

        var renewed = await provider.RenewAsync(jwt, TimeSpan.FromHours(2), TestContext.Current.CancellationToken);

        renewed.Claims.ExpiresAt.ShouldBeGreaterThan(originalExpiry);
        renewed.Claims.InstanceId.ShouldBe(jwt.Claims.InstanceId);
        renewed.Claims.AgentType.ShouldBe(jwt.Claims.AgentType);
        renewed.Claims.Scope.ShouldBe(jwt.Claims.Scope);
    }

}

public sealed class EphemeralIdentityProviderTests
{
    [Fact]
    public async Task IssueAsync_CreatesValidIdentity()
    {
        var provider = new EphemeralIdentityProvider();
        var agentType = "test-agent";
        var scope = new[] { "tier1" };

        var jwt = await provider.IssueAsync(agentType, scope, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt.Token.ShouldNotBeNullOrEmpty();
        jwt.Claims.AgentType.ShouldBe(agentType);
        jwt.Claims.InstanceId.ShouldNotBeNullOrEmpty();
        jwt.Claims.Scope.ShouldBe(scope);
    }

    [Fact]
    public async Task TokenFormat_IsUnsigned()
    {
        var provider = new EphemeralIdentityProvider();

        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        // Ephemeral format: agentType:instanceId
        jwt.Token.ShouldContain(":");
        var parts = jwt.Token.Split(':');
        parts.Length.ShouldBe(2);
    }

    [Fact]
    public async Task VerifyAsync_AlwaysSucceedsForValidFormat()
    {
        var provider = new EphemeralIdentityProvider();
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var verified = await provider.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken);

        verified.ShouldNotBeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsInvalidFormat()
    {
        var provider = new EphemeralIdentityProvider();

        var verified = await provider.VerifyAsync("invalid-format", TestContext.Current.CancellationToken);

        verified.ShouldBeNull();
    }

    [Fact]
    public async Task ResumeAsync_CreatesNewInstanceIdWithResumedFromLink()
    {
        var provider = new EphemeralIdentityProvider();
        var priorId = Guid.NewGuid().ToString("N");

        var resumed = await provider.ResumeAsync("agent", ["scope"], priorId, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        resumed.Claims.ResumedFrom.ShouldBe(priorId);
    }
}

public sealed class LocalJwtIdentityProviderTests
{
    [Fact]
    public async Task IssueAsync_CreatesValidIdentity()
    {
        var provider = new LocalJwtIdentityProvider();
        var agentType = "local-agent";
        var scope = new[] { "tier1", "tier2" };

        var jwt = await provider.IssueAsync(agentType, scope, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt.Token.ShouldNotBeNullOrEmpty();
        jwt.Claims.AgentType.ShouldBe(agentType);
        jwt.Claims.Scope.ShouldBe(scope);
    }

    [Fact]
    public async Task TokenFormat_IsRS256()
    {
        var provider = new LocalJwtIdentityProvider();

        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        // JWT has 3 parts: header.payload.signature
        var parts = jwt.Token.Split('.');
        parts.Length.ShouldBe(3);
    }

    [Fact]
    public async Task VerifyAsync_ValidatesSignature()
    {
        var provider = new LocalJwtIdentityProvider();
        var jwt = await provider.IssueAsync("agent", ["scope"], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var verified = await provider.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken);

        verified.ShouldNotBeNull();
        verified!.AgentType.ShouldBe("agent");
        verified.Scope.ShouldBe(new[] { "scope" });
    }

    [Fact]
    public async Task VerifyAsync_RejectsCorruptedSignature()
    {
        var provider = new LocalJwtIdentityProvider();
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var parts = jwt.Token.Split('.');
        var corrupted = $"{parts[0]}.{parts[1]}.corrupted";

        var verified = await provider.VerifyAsync(corrupted, TestContext.Current.CancellationToken);

        verified.ShouldBeNull();
    }

    [Fact]
    public async Task RenewAsync_PreservesInstanceId()
    {
        var provider = new LocalJwtIdentityProvider();
        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var originalInstanceId = jwt.Claims.InstanceId;

        var renewed = await provider.RenewAsync(jwt, TimeSpan.FromHours(2), TestContext.Current.CancellationToken);

        renewed.Claims.InstanceId.ShouldBe(originalInstanceId);
    }

    [Fact]
    public async Task ResumeAsync_CreatesNewInstanceIdWithResumedFromLink()
    {
        var provider = new LocalJwtIdentityProvider();
        var priorId = Guid.NewGuid().ToString("N");

        var resumed = await provider.ResumeAsync("agent", ["scope"], priorId, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        resumed.Claims.InstanceId.ShouldNotBe(priorId);
        resumed.Claims.ResumedFrom.ShouldBe(priorId);
    }

    [Fact]
    public async Task ResumeAsync_TokenIsVerifiable()
    {
        var provider = new LocalJwtIdentityProvider();
        var resumed = await provider.ResumeAsync("agent", ["scope"], "prior-id", TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var verified = await provider.VerifyAsync(resumed.Token, TestContext.Current.CancellationToken);

        verified.ShouldNotBeNull();
        verified!.ResumedFrom.ShouldBe("prior-id");
    }
}

public sealed class EntraIdentityProviderTests
{
    [Fact]
    public void Constructor_RequiresTenantId()
    {
        var ex = Should.Throw<ArgumentException>(() => new EntraIdentityProvider(""));

        ex.ParamName.ShouldBe("tenantId");
    }

    [Fact]
    public async Task IssueAsync_CreatesValidIdentity()
    {
        var provider = new EntraIdentityProvider("tenant-123");
        var agentType = "entra-agent";
        var scope = new[] { "scope1" };

        var jwt = await provider.IssueAsync(agentType, scope, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt.Token.ShouldNotBeNullOrEmpty();
        jwt.Claims.InstanceId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TokenFormat_IncludesTenantId()
    {
        var tenantId = "my-tenant";
        var provider = new EntraIdentityProvider(tenantId);

        var jwt = await provider.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        jwt.Token.ShouldContain(tenantId);
    }

    [Fact]
    public async Task VerifyAsync_RejectsWrongTenant()
    {
        var provider1 = new EntraIdentityProvider("tenant-1");
        var provider2 = new EntraIdentityProvider("tenant-2");

        var jwt = await provider1.IssueAsync("agent", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        var verified = await provider2.VerifyAsync(jwt.Token, TestContext.Current.CancellationToken);

        verified.ShouldBeNull();
    }

    [Fact]
    public async Task ResumeAsync_CreatesNewInstanceIdWithResumedFromLink()
    {
        var provider = new EntraIdentityProvider("tenant");
        var priorId = Guid.NewGuid().ToString("N");

        var resumed = await provider.ResumeAsync("agent", ["scope"], priorId, TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        resumed.Claims.ResumedFrom.ShouldBe(priorId);
    }
}

public sealed class AgentIdentityTests
{
    [Fact]
    public void AgentIdentity_CanBeCreatedWithoutResumedFrom()
    {
        var identity = new AgentIdentity(
            "agent-type",
            "instance-id",
            new[] { "scope" },
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));

        identity.ResumedFrom.ShouldBeNull();
    }

    [Fact]
    public void AgentIdentity_CanBeCreatedWithResumedFrom()
    {
        var resumedFrom = "prior-instance-id";
        var identity = new AgentIdentity(
            "agent-type",
            "instance-id",
            new[] { "scope" },
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            resumedFrom);

        identity.ResumedFrom.ShouldBe(resumedFrom);
    }

    [Fact]
    public void AgentIdentity_IsRecord()
    {
        var identity = new AgentIdentity(
            "agent",
            "id",
            new[] { "scope" },
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));

        // Records have meaningful ToString
        identity.ToString().ShouldContain("agent");
    }
}

public sealed class AgentJwtTests
{
    [Fact]
    public void AgentJwt_WrapsTokenAndClaims()
    {
        var token = "jwt-token-string";
        var claims = new AgentIdentity(
            "agent", "id", ["scope"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1));

        var jwt = new AgentJwt(token, claims);

        jwt.Token.ShouldBe(token);
        jwt.Claims.ShouldBe(claims);
    }
}
