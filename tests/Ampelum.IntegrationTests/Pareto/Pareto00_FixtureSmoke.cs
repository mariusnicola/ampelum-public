using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using Ampelum.IntegrationTests.Skips;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Smoke test that proves the IntegrationTests project + ParetoTestFixture wire
/// together. Always runs; does not depend on any env var.
/// </summary>
public sealed class Pareto00_FixtureSmoke : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    public Pareto00_FixtureSmoke(ParetoTestFixture fx) => _fx = fx;

    [Fact]
    public void Fixture_WiresUp()
    {
        _fx.Lm.ShouldNotBeNull();
        _fx.Audit.ShouldNotBeNull();
        ParetoTestFixture.MaxTestCostCents.ShouldBe(5m);
    }

    [Fact]
    public void SkipPredicates_AreAddressable()
    {
        SkipUnlessRealLm.EnvVarName.ShouldBe("AMPELUM_INTEGRATION_OPENROUTER_KEY");
        SkipUnlessAotPublished.EnvVarName.ShouldBe("AMPELUM_AOT_PROBE_PATH");
        SkipUnlessLinux.Reason.ShouldNotBeNullOrEmpty();
    }
}
