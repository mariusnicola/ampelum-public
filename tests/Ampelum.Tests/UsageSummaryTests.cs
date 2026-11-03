using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

#pragma warning disable AMPELUM_V09_PREVIEW

public sealed class ChatCostSummaryTests
{
    [Fact]
    public void Zero_HasAllZeros()
    {
        ChatCostSummary.Zero.InputTokens.ShouldBe(0L);
        ChatCostSummary.Zero.OutputTokens.ShouldBe(0L);
        ChatCostSummary.Zero.EstimatedCostUsd.ShouldBe(0m);
    }

    [Fact]
    public void Add_SumsTokensAndCosts()
    {
        var a = new ChatCostSummary(100, 50, 0.01m);
        var b = new ChatCostSummary(200, 100, 0.02m);
        var sum = a + b;
        sum.InputTokens.ShouldBe(300L);
        sum.OutputTokens.ShouldBe(150L);
        sum.EstimatedCostUsd.ShouldBe(0.03m);
    }

    [Fact]
    public void Add_BothNullCost_ReturnsNull()
    {
        var a = new ChatCostSummary(100, 50, null);
        var b = new ChatCostSummary(200, 100, null);
        (a + b).EstimatedCostUsd.ShouldBeNull();
    }

    [Fact]
    public void Add_OneNullCost_ReturnsOtherValue()
    {
        var a = new ChatCostSummary(100, 50, null);
        var b = new ChatCostSummary(200, 100, 0.02m);
        (a + b).EstimatedCostUsd.ShouldBe(0.02m);
    }

    [Fact]
    public void ModelRateTable_EstimatesCorrectly()
    {
        var rates = new ModelRateTable(InputPerToken: 0.000001m, OutputPerToken: 0.000003m);
        rates.Estimate(1000, 500).ShouldBe(1000 * 0.000001m + 500 * 0.000003m);
    }
}

public sealed class ModelRateTablePerModelTests
{
    static readonly ModelRateTable Default = new(InputPerToken: 0.000001m, OutputPerToken: 0.000003m);

    [Fact]
    public void EstimateCost_UsesTheRatesOfTheNamedModel()
    {
        var table = Default with
        {
            PerModel = new Dictionary<string, ModelRateTable>
            {
                ["big"] = new(InputPerToken: 0.00001m, OutputPerToken: 0.00003m),
            },
        };

        table.EstimateCost("big", 1000, 500).ShouldBe(1000 * 0.00001m + 500 * 0.00003m);
        table.EstimateCost("small", 1000, 500).ShouldBe(1000 * 0.000001m + 500 * 0.000003m);
    }

    [Fact]
    public void EstimateCost_WithNoPerModelRates_UsesTheDefaultPairForAnyModel()
    {
        Default.EstimateCost("anything", 1000, 500).ShouldBe(Default.Estimate(1000, 500));
    }

    [Fact]
    public void EstimateCost_MatchesModelIdExactly()
    {
        var table = Default with
        {
            PerModel = new Dictionary<string, ModelRateTable>
            {
                ["big"] = new(InputPerToken: 1m, OutputPerToken: 1m),
            },
        };

        table.EstimateCost("BIG", 1, 1).ShouldBe(Default.Estimate(1, 1));
    }
}
