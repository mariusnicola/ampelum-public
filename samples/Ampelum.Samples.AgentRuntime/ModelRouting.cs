using Ampelum.Samples.Shared;
using SignalGrid.Ampelum.Agent;

namespace Ampelum.Samples.AgentRuntime;

/// <summary>Model ids behind the three routing tiers.</summary>
public sealed record ModelTiers(string Fast, string Balanced, string Capable, string Provider)
{
    /// <summary>
    /// Reads the tiers from the sample settings. Two model ids are configured, so the larger one serves the
    /// balanced and capable tiers and the second one serves the fast tier.
    /// </summary>
    public static ModelTiers FromConfig(SampleConfig config) => new(
        Fast: config.Require("OPENROUTER_MODEL_2"),
        Balanced: config.Require("OPENROUTER_MODEL_1"),
        Capable: config.Require("OPENROUTER_MODEL_1"),
        Provider: "openrouter");

    /// <summary>The model id of one tier.</summary>
    public string For(ModelTier tier) => tier switch
    {
        ModelTier.Fast => Fast,
        ModelTier.Balanced => Balanced,
        _ => Capable,
    };

    /// <summary>Builds the router: task type rules come from the project policy, everything else gets the balanced tier.</summary>
    public IModelRouter CreateRouter(TriagePolicy policy) => new ConfiguredModelRouter(new ModelRoutingTable
    {
        Rules = policy.ToRules(),
        FallbackTier = ModelTier.Balanced,
        Tiers = new Dictionary<ModelTier, TierConfig>
        {
            [ModelTier.Fast] = new() { PrimaryModelId = Fast, Provider = Provider },
            [ModelTier.Balanced] = new() { PrimaryModelId = Balanced, Provider = Provider },
            [ModelTier.Capable] = new() { PrimaryModelId = Capable, Provider = Provider },
        },
    });
}
