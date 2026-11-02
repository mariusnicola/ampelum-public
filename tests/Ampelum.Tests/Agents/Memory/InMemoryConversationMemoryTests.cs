using System;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Memory;

public class InMemoryConversationMemoryTests
{
    [Fact]
    public async Task Capacity_evicts_oldest()
    {
        var mem = new InMemoryConversationMemory(capacity: 5);
        for (var i = 0; i < 7; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", i.ToString()), TestContext.Current.CancellationToken);
        var recent = await mem.GetRecentAsync(10, TestContext.Current.CancellationToken);
        recent.Count.ShouldBe(5);
        // FIFO eviction — oldest (0,1) dropped, retained 2..6
        recent[0].Content.ShouldBe("2");
        recent[4].Content.ShouldBe("6");
    }

    [Fact]
    public async Task GetRecent_returns_last_n()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        for (var i = 0; i < 5; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", i.ToString()), TestContext.Current.CancellationToken);
        var r = await mem.GetRecentAsync(3, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(3);
        r[0].Content.ShouldBe("2");
        r[1].Content.ShouldBe("3");
        r[2].Content.ShouldBe("4");
    }

    [Fact]
    public async Task Query_falls_back_to_recent()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        for (var i = 0; i < 4; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", i.ToString()), TestContext.Current.CancellationToken);
        var q = await mem.QueryAsync("anything", 2, TestContext.Current.CancellationToken);
        q.Count.ShouldBe(2);
        q[0].Content.ShouldBe("2");
        q[1].Content.ShouldBe("3");
    }

    [Fact]
    public void Capacity_zero_throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InMemoryConversationMemory(capacity: 0));
    }

    [Fact]
    public void Capacity_negative_throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InMemoryConversationMemory(capacity: -1));
    }

    [Fact]
    public async Task GetRecent_with_n_larger_than_count_returns_all()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        await mem.AppendAsync(new AgentMessage("a", "r", "x"), TestContext.Current.CancellationToken);
        var r = await mem.GetRecentAsync(50, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetRecent_zero_returns_empty()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        await mem.AppendAsync(new AgentMessage("a", "r", "x"), TestContext.Current.CancellationToken);
        var r = await mem.GetRecentAsync(0, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetRecent_negative_returns_empty()
    {
        var mem = new InMemoryConversationMemory(capacity: 10);
        await mem.AppendAsync(new AgentMessage("a", "r", "x"), TestContext.Current.CancellationToken);
        var r = await mem.GetRecentAsync(-3, TestContext.Current.CancellationToken);
        r.Count.ShouldBe(0);
    }
}
