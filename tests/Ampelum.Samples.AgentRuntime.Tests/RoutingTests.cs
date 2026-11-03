using Ampelum.Samples.AgentRuntime;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentRuntime.Tests;

public sealed class RoutingTests : IDisposable
{
    readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("classify", ModelTier.Fast, "fake-fast-model")]
    [InlineData("analyse", ModelTier.Balanced, "fake-balanced-model")]
    [InlineData("refine", ModelTier.Capable, "fake-capable-model")]
    [InlineData("plan", ModelTier.Capable, "fake-capable-model")]
    [InlineData("anything-else", ModelTier.Balanced, "fake-balanced-model")]
    public void Router_PicksTheTierTheProjectPolicyNames_AndBalancedForUnknownWork(string taskType, ModelTier tier, string modelId)
    {
        var router = FakeTriageModel.Tiers.CreateRouter(TriagePolicy.Load(_workspace.Root));

        var route = router.Route(new TaskSignal { TaskType = taskType });

        route.Tier.ShouldBe(tier);
        route.ModelId.ShouldBe(modelId);
    }

    [Fact]
    public async Task EveryModelCallOfARun_CarriesTheModelIdOfItsTier()
    {
        var model = new FakeTriageModel();

        await TriageScenario.RunAsync(_workspace.Directory, model, FakeTriageModel.Tiers, ct: TestContext.Current.CancellationToken);

        ModelIds(model, "Select the best skill(s) for").ShouldBe(["fake-fast-model", "fake-fast-model"]);
        ModelIds(model, "Assess the issue from the brief").ShouldBe(["fake-balanced-model", "fake-balanced-model", "fake-balanced-model"]);
        ModelIds(model, "Refine the report draft").ShouldBe(["fake-capable-model", "fake-capable-model"]);
        ModelIds(model, "Create a workflow YAML").ShouldBe(["fake-capable-model"]);
        ModelIds(model, "Decompose the intent").ShouldBe(["fake-capable-model"]);
        model.Calls.ShouldAllBe(call => call.ModelId != null);
    }

    static IEnumerable<string?> ModelIds(FakeTriageModel model, string promptMarker) =>
        model.Calls.Where(c => c.Prompt.Contains(promptMarker, StringComparison.Ordinal)).Select(c => c.ModelId);
}
