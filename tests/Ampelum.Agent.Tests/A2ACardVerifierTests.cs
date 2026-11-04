using Shouldly;
using SignalGrid.Ampelum.Agent.A2A;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class A2ACardVerifierTests
{
    static A2AAgentCard Card(string name, IReadOnlyList<A2AAgentCapability>? capabilities = null, string scheme = "bearer", string? token = null) => new()
    {
        Name = name,
        Description = "d",
        Version = "1",
        Url = "https://example.com",
        Capabilities = capabilities,
        AuthenticationSchemes = token is null ? null : [new A2AAuthenticationScheme { Scheme = scheme, Credentials = token }],
    };

    [Fact]
    public void VerifyCapabilities_EveryCustomEntry_GetsAWarningNamingIt()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var hostile = new A2AAgentCapability
        {
            Streaming = true,
            Custom = new Dictionary<string, object?> { ["exec"] = "rm -rf /", ["scope"] = "*" },
        };

        var warnings = verifier.VerifyCapabilities(Card("a", [hostile]));

        warnings.Select(w => w.CapabilityName).Order().ShouldBe(["exec", "scope"]);
        warnings.ShouldAllBe(w => w.Warning.Contains("not a known capability", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifyCapabilities_KnownFlagsOnly_OrAnEmptyCustomMap_GetNoWarning()
    {
        var verifier = new DefaultAgentCardVerifier(new DefaultAgentIdentityProvider(TestSigningKey.Create()));
        var plain = new A2AAgentCapability { Streaming = true, PushNotifications = true, StateSync = true, LongRunningTasks = true };
        var emptyCustom = new A2AAgentCapability { Custom = new Dictionary<string, object?>() };

        verifier.VerifyCapabilities(Card("a", [plain, emptyCustom])).ShouldBeEmpty();
        verifier.VerifyCapabilities(Card("a", [])).ShouldBeEmpty();
        verifier.VerifyCapabilities(Card("a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task VerifyIdentityAsync_ATokenIssuedForAnotherAgent_ReturnsNull()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("payroll-agent", ["payments"], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var identity = await verifier.VerifyIdentityAsync(Card("supplier-clause-reviewer", token: jwt.Token), TestContext.Current.CancellationToken);

        identity.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyIdentityAsync_ATokenIssuedForTheCardsAgent_ReturnsItsIdentity_WhateverTheCase()
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("payroll-agent", ["payments"], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var identity = await verifier.VerifyIdentityAsync(Card("Payroll-Agent", token: jwt.Token), TestContext.Current.CancellationToken);

        identity.ShouldNotBeNull().AgentType.ShouldBe("payroll-agent");
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("BEARER")]
    [InlineData("bearer")]
    public async Task VerifyIdentityAsync_TheSchemeNameIsMatchedWithoutRegardToCase(string scheme)
    {
        var issuer = new DefaultAgentIdentityProvider(TestSigningKey.Create());
        var verifier = new DefaultAgentCardVerifier(issuer);
        var jwt = await issuer.IssueAsync("a", [], TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var identity = await verifier.VerifyIdentityAsync(Card("a", scheme: scheme, token: jwt.Token), TestContext.Current.CancellationToken);

        identity.ShouldNotBeNull();
    }
}
