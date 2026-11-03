using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class UsageTests
{
    [Fact]
    public async Task Usage_AddsTokensAcrossCalls_AndPricesThemWithTheRatesTheCallerSupplied()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "Fine.", inputTokens: 1000, outputTokens: 200);
        // The tool turn is scripted at 100 in and 20 out; the final answer at 300 in and 50 out.
        FakeScript.EnqueueToolTurn(h.Chat, ("c1", ToolCatalog.Search,
            new Dictionary<string, object?> { ["query"] = "Payroll", ["credential"] = FakeScript.CredentialAlias }));
        FakeScript.EnqueueFinal(h.Chat, "Done.", inputTokens: 300, outputTokens: 50);

        await h.Gateway.AskAsync("A question?", TestContext.Current.CancellationToken);
        await h.Gateway.AskWithToolsAsync("Find Payroll.", TestContext.Current.CancellationToken);
        var usage = h.Gateway.Usage();

        usage.Tokens.RequestCount.ShouldBe(3);
        usage.Tokens.PromptTokens.ShouldBe(1400);
        usage.Tokens.CompletionTokens.ShouldBe(270);
        // 1400 * 0.000001 + 270 * 0.000003
        usage.Cost.EstimatedCostUsd.ShouldBe(0.00221m);

        var ask = usage.Tokens.PerScope!["ask"];
        ask.PromptTokens.ShouldBe(1000);
        ask.RequestCount.ShouldBe(1);
        var withTools = usage.Tokens.PerScope["ask-with-tools"];
        withTools.PromptTokens.ShouldBe(400);
        withTools.CompletionTokens.ShouldBe(70);
        withTools.RequestCount.ShouldBe(2);
        Harness.Rates.Estimate(withTools.PromptTokens, withTools.CompletionTokens).ShouldBe(0.00061m);
    }

    [Fact]
    public async Task Cost_FollowsTheRatesInTheOptions_NotAnyBuiltInPrice()
    {
        var doubled = new ModelRateTable(Harness.Rates.InputPerToken * 2, Harness.Rates.OutputPerToken * 2);
        await using var h = await Harness.StartAsync(o => o with { Rates = doubled });
        FakeScript.EnqueueAnswer(h.Chat, "Fine.", inputTokens: 1000, outputTokens: 200);

        await h.Gateway.AskAsync("A question?", TestContext.Current.CancellationToken);

        h.Gateway.Usage().Cost.EstimatedCostUsd.ShouldBe(0.0032m);
    }

    [Theory]
    [InlineData("2e-7", "0.0000002")]
    [InlineData("0.000003", "0.000003")]
    [InlineData("3E-6", "0.000003")]
    public void RateSetting_ReadsPlainAndExponentNotation_WhateverTheMachineCulture(string text, string expected)
    {
        RateSetting.Parse(text).ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
