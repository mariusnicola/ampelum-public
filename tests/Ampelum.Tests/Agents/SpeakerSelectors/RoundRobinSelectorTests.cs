using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.SpeakerSelectors;

public class RoundRobinSelectorTests
{
    sealed class StubAgent(string name) : IAgent
    {
        public string Name { get; } = name;
        public string Role { get; } = name + "-role";
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, System.Threading.CancellationToken ct) =>
            ValueTask.FromResult(new AgentMessage(Name, Role, ""));
    }

    static IReadOnlyList<IAgent> ABC() => [new StubAgent("a"), new StubAgent("b"), new StubAgent("c")];

    [Fact]
    public async Task Cycles_through_three_agents()
    {
        var sel = new RoundRobinSelector();
        var agents = ABC();
        for (var i = 0; i < 6; i++)
        {
            var ctx = new ConversationContext([], memory: null, turnIndex: i);
            var pick = await sel.SelectAsync(ctx, agents, TestContext.Current.CancellationToken);
            pick.Name.ShouldBe(new[] { "a", "b", "c" }[i % 3]);
        }
    }

    [Fact]
    public async Task Single_agent_always_selected()
    {
        var sel = new RoundRobinSelector();
        var agents = new IAgent[] { new StubAgent("solo") };
        for (var i = 0; i < 4; i++)
        {
            var ctx = new ConversationContext([], memory: null, turnIndex: i);
            var pick = await sel.SelectAsync(ctx, agents, TestContext.Current.CancellationToken);
            pick.Name.ShouldBe("solo");
        }
    }

    [Fact]
    public async Task Empty_agents_throws_argument_exception()
    {
        var sel = new RoundRobinSelector();
        var ctx = new ConversationContext([], memory: null, turnIndex: 0);
        await Should.ThrowAsync<ArgumentException>(async () =>
            await sel.SelectAsync(ctx, [], TestContext.Current.CancellationToken));
    }
}
