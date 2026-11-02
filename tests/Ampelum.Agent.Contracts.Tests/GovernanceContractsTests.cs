using SignalGrid.Ampelum.Agent.Contracts;
using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Contracts.Tests;

public sealed class GovernanceContractsTests
{
    [Fact]
    public void ToolPolicyDecision_Captures_Confirmation_Gate()
    {
        var decision = new ToolPolicyDecision(
            ToolPolicyDisposition.RequireConfirmation,
            "External contact needs approval.",
            "outreach-policy-v1",
            DateTimeOffset.Parse("2026-07-27T10:00:00Z"));

        decision.Disposition.ShouldBe(ToolPolicyDisposition.RequireConfirmation);
    }

    [Fact]
    public void ObservationTokenIssuance_Binds_Token_To_Scope_And_Expiry()
    {
        var issuance = new ObservationTokenIssuance(
            "token-1",
            "prospect:42",
            "read-prospect",
            "execution-1",
            DateTimeOffset.Parse("2026-07-27T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T10:10:00Z"));

        issuance.ExpiresAt.ShouldBeGreaterThan(issuance.IssuedAt);
    }
}
