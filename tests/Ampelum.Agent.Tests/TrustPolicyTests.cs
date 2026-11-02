using Shouldly;
using Xunit;

namespace SignalGrid.Ampelum.Agent.Tests;

public sealed class TrustPolicyTests
{
    [Fact]
    public void Default_SuccessThresholdForEscalation_Is_Five()
    {
        var policy = new TrustPolicy();
        policy.SuccessThresholdForEscalation.ShouldBe(5);
    }

    [Fact]
    public void Default_ViolationsForDemotion_Is_One()
    {
        var policy = new TrustPolicy();
        policy.ViolationsForDemotion.ShouldBe(1);
    }

    [Fact]
    public void Default_EvaluationWindowRuns_Is_Ten()
    {
        var policy = new TrustPolicy();
        policy.EvaluationWindowRuns.ShouldBe(10);
    }

    [Fact]
    public void Default_ViolationsForRevocation_Is_Three()
    {
        var policy = new TrustPolicy();
        policy.ViolationsForRevocation.ShouldBe(3);
    }

    [Fact]
    public void Default_InactivityDecayDays_Is_Ninety()
    {
        var policy = new TrustPolicy();
        policy.InactivityDecayDays.ShouldBe(90);
    }

    [Fact]
    public void Can_Override_Defaults_With_Init()
    {
        var policy = new TrustPolicy
        {
            SuccessThresholdForEscalation = 3,
            ViolationsForDemotion = 2,
            EvaluationWindowRuns = 5,
            ViolationsForRevocation = 1,
            InactivityDecayDays = 30,
        };

        policy.SuccessThresholdForEscalation.ShouldBe(3);
        policy.ViolationsForDemotion.ShouldBe(2);
        policy.EvaluationWindowRuns.ShouldBe(5);
        policy.ViolationsForRevocation.ShouldBe(1);
        policy.InactivityDecayDays.ShouldBe(30);
    }
}