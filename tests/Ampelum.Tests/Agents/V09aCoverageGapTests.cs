using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.SpeakerSelectors;
using SignalGrid.Ampelum.Agents.Tools;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.V09aCoverage;

public class V09aCoverageGapTests
{
    [Fact]
    public void AgentToolResult_record_round_trips()
    {
        var meta = new Dictionary<string, string> { ["k"] = "v" };
        var a = new AgentToolResult(true, "out", null, meta);
        a.Success.ShouldBeTrue();
        a.Output.ShouldBe("out");
        a.ErrorMessage.ShouldBeNull();
        a.Metadata["k"].ShouldBe("v");
        var b = new AgentToolResult(true, "out", null, meta);
        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        var c = a with { Output = "other" };
        c.Output.ShouldBe("other");
        a.Output.ShouldBe("out");
    }

    sealed class StubAgent : IAgent
    {
        public string Name => "x";
        public string Role => "x";
        public ValueTask<AgentMessage> ReplyAsync(ConversationContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(new AgentMessage(Name, Role, "hello") { IsTerminal = true });
    }

    [Fact]
    public async Task GroupChat_with_memory_appends_system_and_agent()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        var chat = new GroupChat([new StubAgent()], new RoundRobinSelector(), mem);
        await chat.RunAsync("kickoff", maxTurns: 5, TestContext.Current.CancellationToken);
        var stored = await mem.GetRecentAsync(10, TestContext.Current.CancellationToken);
        stored.Count.ShouldBe(2);
        stored[0].SenderName.ShouldBe("system");
        stored[1].SenderName.ShouldBe("x");
    }

    [Fact]
    public async Task RoleBasedSelector_throws_on_empty_agents()
    {
        var sel = new RoleBasedSelector(["planner"]);
        var ctx = new ConversationContext([], memory: null, turnIndex: 0);
        await Should.ThrowAsync<ArgumentException>(async () =>
            await sel.SelectAsync(ctx, [], TestContext.Current.CancellationToken));
    }

    sealed class EmptyMem : IConversationMemory
    {
        public ValueTask AppendAsync(AgentMessage m, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
        public ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string q, int k, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
    }

    [Fact]
    public async Task Composite_query_returns_merged_when_below_topk()
    {
        var c = new CompositeConversationMemory(new EmptyMem(), new EmptyMem());
        var q = await c.QueryAsync("anything", topK: 5, TestContext.Current.CancellationToken);
        q.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Composite_query_zero_topk_returns_empty()
    {
        var c = new CompositeConversationMemory(new EmptyMem());
        var q = await c.QueryAsync("anything", topK: 0, TestContext.Current.CancellationToken);
        q.Count.ShouldBe(0);
    }

    [Fact]
    public async Task EpisodicMemory_GetRecent_zero_returns_empty()
    {
        var ep = new EpisodicMemory(new FakeChatClient(), threshold: 5, windowSize: 5);
        await ep.AppendAsync(new AgentMessage("a", "r", "x"), TestContext.Current.CancellationToken);
        var r = await ep.GetRecentAsync(0, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(0);
    }
}
