using Shouldly;
using Xunit;
using SignalGrid.Ampelum.Agent;

namespace SignalGrid.Ampelum.Agent.Tests.Routing;

public sealed class ModelRouterTests
{
    private static ConfiguredModelRouter DefaultRouter() =>
        new ConfiguredModelRouter(ModelRoutingTable.WithDefaultRules(TestTiers()));

    private static Dictionary<ModelTier, TierConfig> TestTiers() => new()
    {
        [ModelTier.Fast] = new TierConfig { PrimaryModelId = "test-fast", FallbackModelId = "test-balanced", Provider = "test" },
        [ModelTier.Balanced] = new TierConfig { PrimaryModelId = "test-balanced", FallbackModelId = "test-fast", Provider = "test" },
        [ModelTier.Capable] = new TierConfig { PrimaryModelId = "test-capable", FallbackModelId = "test-balanced", Provider = "test" },
    };

    [Fact]
    public void Route_ExplicitCapable_ReturnsCapableTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "implement", RequiredTier = ModelTier.Capable };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Capable);
        route.ModelId.ShouldNotBeNull();
    }

    [Fact]
    public void Route_ImplementTaskType_ReturnsFastTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "implement" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Fast);
    }

    [Fact]
    public void Route_SpecWriteTaskType_ReturnsCapableTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "spec_write" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Capable);
    }

    [Fact]
    public void Route_RequiresReasoning_ReturnsCapableTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "unknown_task", RequiresReasoning = true };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Capable);
    }

    [Fact]
    public void Route_IsInteractive_ReturnsFastTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "unknown_task", IsInteractive = true };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Fast);
    }

    [Fact]
    public void Route_UnknownTask_ReturnsFallbackBalanced()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "completely_unknown" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Balanced);
    }

    [Fact]
    public void Route_Always_ReturnsNonNullModelIdAndProvider()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "review" };

        var route = router.Route(signal);

        route.ModelId.ShouldNotBeNullOrEmpty();
        route.Provider.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void RouteWithFallback_PreferredUnavailable_StepsDownToLowerTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "spec_write" };

        // Mark Capable and Balanced as unavailable, only Fast is available
        var route = router.RouteWithFallback(signal, tier => tier == ModelTier.Fast);

        // Should step down from Capable to Fast
        route.Tier.ShouldBe(ModelTier.Fast);
    }

    [Fact]
    public void RouteWithFallback_AllUnavailable_Throws()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "implement" };

        Should.Throw<InvalidOperationException>(() => router.RouteWithFallback(signal, _ => false));
    }

    [Fact]
    public void Route_NullSignal_Throws()
    {
        var router = DefaultRouter();

        Should.Throw<ArgumentNullException>(() => router.Route(null!));
    }

    [Fact]
    public void Constructor_NullTable_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new ConfiguredModelRouter(null!));
    }

    [Fact]
    public void Route_RequiredTierOverridesTaskTypeRule()
    {
        var router = DefaultRouter();
        // "implement" normally routes to Fast, but we override to Capable
        var signal = new TaskSignal { TaskType = "implement", RequiredTier = ModelTier.Capable };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Capable);
    }

    [Fact]
    public void Route_CustomTable_UsesCustomRules()
    {
        var table = new ModelRoutingTable
        {
            Rules =
            [
                new RoutingRule { TaskType = "custom_task", Tier = ModelTier.Fast }
            ],
            FallbackTier = ModelTier.Balanced,
            Tiers = new Dictionary<ModelTier, TierConfig>
            {
                [ModelTier.Fast] = new TierConfig { PrimaryModelId = "my-fast-model", Provider = "custom" },
                [ModelTier.Balanced] = new TierConfig { PrimaryModelId = "my-balanced-model", Provider = "custom" },
                [ModelTier.Capable] = new TierConfig { PrimaryModelId = "my-capable-model", Provider = "custom" },
            }
        };
        var router = new ConfiguredModelRouter(table);
        var signal = new TaskSignal { TaskType = "custom_task" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Fast);
        route.ModelId.ShouldBe("my-fast-model");
        route.Provider.ShouldBe("custom");
    }

    [Fact]
    public void RouteWithFallback_UsesAvailabilityFunction()
    {
        var router = DefaultRouter();
        // spec_write prefers Capable tier
        var signal = new TaskSignal { TaskType = "spec_write" };

        // Only Balanced is available (Capable preferred but unavailable, Fast also unavailable)
        var route = router.RouteWithFallback(signal, tier => tier == ModelTier.Balanced);

        route.Tier.ShouldBe(ModelTier.Balanced);
    }

    [Fact]
    public void Route_ReviewTaskType_ReturnsBalancedTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "review" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Balanced);
    }

    [Fact]
    public void Route_SynthesizeSkillTaskType_ReturnsCapableTier()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "synthesize_skill" };

        var route = router.Route(signal);

        route.Tier.ShouldBe(ModelTier.Capable);
    }

    [Fact]
    public void Route_EstimatedTokens_DoesNotAffectRouting()
    {
        var router = DefaultRouter();
        var signal1 = new TaskSignal { TaskType = "implement", EstimatedTokens = 0 };
        var signal2 = new TaskSignal { TaskType = "implement", EstimatedTokens = 100000 };

        var route1 = router.Route(signal1);
        var route2 = router.Route(signal2);

        route1.Tier.ShouldBe(route2.Tier);
    }

    [Fact]
    public void RouteWithFallback_RequiredTierStillObeysFallback()
    {
        var router = DefaultRouter();
        var signal = new TaskSignal { TaskType = "implement", RequiredTier = ModelTier.Capable };

        var route = router.RouteWithFallback(signal, tier => tier == ModelTier.Capable);

        route.Tier.ShouldBe(ModelTier.Capable);
    }
}
