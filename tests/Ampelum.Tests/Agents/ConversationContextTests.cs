using System.Collections.Generic;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents;

public class ConversationContextTests
{
    [Fact]
    public void Defensive_copy_isolates_history_from_caller_mutation()
    {
        var src = new List<AgentMessage>
        {
            new("a", "r", "x"),
            new("b", "r", "y"),
        };
        var ctx = new ConversationContext(src, memory: null, turnIndex: 0);
        src.Add(new AgentMessage("c", "r", "z"));
        ctx.History.Count.ShouldBe(2);
    }

    [Fact]
    public void Turn_index_and_memory_round_trip()
    {
        var ctx = new ConversationContext([], memory: null, turnIndex: 5);
        ctx.TurnIndex.ShouldBe(5);
        ctx.Memory.ShouldBeNull();
        ctx.History.Count.ShouldBe(0);
    }

    [Fact]
    public void History_preserves_order()
    {
        var src = new[]
        {
            new AgentMessage("a", "r", "1"),
            new AgentMessage("b", "r", "2"),
            new AgentMessage("c", "r", "3"),
        };
        var ctx = new ConversationContext(src, memory: null, turnIndex: 3);
        ctx.History[0].Content.ShouldBe("1");
        ctx.History[1].Content.ShouldBe("2");
        ctx.History[2].Content.ShouldBe("3");
    }
}
