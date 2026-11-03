using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Agent;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

public sealed class ToolAgentTests
{
    const string ShippedText = "Order A-1001: Shipped, carrier NorthPost, delivery in 2 business days.";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpTools_AreOfferedToTheModel_AndTheToolResultReachesIt()
    {
        var model = FakeScript.ToolModel("A-1001");
        using var agent = new ToolAgent(model, new OrderToolSource());

        var answer = await agent.AskAsync("Where is order A-1001?", Ct);

        answer.ShouldBe(ShippedText);
        model.Inner.RecordedCalls.Count.ShouldBe(2);
        model.Inner.RecordedCalls[0].Options!.Tools!.Select(t => t.Name).ShouldBe(["lookup_order"]);
        var toolResult = model.Inner.RecordedCalls[1].Messages
            .SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        toolResult.Result!.ToString().ShouldBe(ShippedText);
    }

    [Fact]
    public async Task UnknownOrder_ReachesTheModelAsNotFound()
    {
        var model = FakeScript.ToolModel("A-9999");
        using var agent = new ToolAgent(model, new OrderToolSource());

        var answer = await agent.AskAsync("Where is order A-9999?", Ct);

        answer.ShouldBe("No order with id A-9999.");
    }

    [Fact]
    public async Task ToolNames_ComeFromTheToolSource()
    {
        using var agent = new ToolAgent(FakeScript.ToolModel("A-1001"), new OrderToolSource());

        (await agent.ToolNamesAsync(Ct)).ShouldBe(["lookup_order"]);
    }

    [Fact]
    public async Task Survey_IgnoresTheTierTheServerDeclares()
    {
        var declared = await new OrderToolTransport().ListToolsAsync(Ct);
        declared.Single().DangerTier.ShouldBe(nameof(DangerTier.Safe));

        var surveyed = await ToolAgent.SurveyAsync(new OrderToolTransport(), OrderToolTransport.ServerUri, Ct);

        var tool = surveyed.ShouldHaveSingleItem();
        tool.Name.ShouldBe("lookup_order");
        tool.DangerTier.ShouldBe(DangerTier.Catastrophic);
        tool.RequiresConfirmation.ShouldBeTrue();
    }
}
