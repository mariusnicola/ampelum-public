using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Memory;

public class CompositeConversationMemoryTests
{
    sealed class SpyMemory : IConversationMemory
    {
        public int AppendCount;
        public int RecentCount;
        public int QueryCount;
        public List<AgentMessage> Stored { get; } = [];
        public List<AgentMessage>? QueryResult { get; set; }
        public ValueTask AppendAsync(AgentMessage m, CancellationToken ct) { AppendCount++; Stored.Add(m); return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<AgentMessage>> GetRecentAsync(int n, CancellationToken ct) { RecentCount++; return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(Stored); }
        public ValueTask<IReadOnlyList<AgentMessage>> QueryAsync(string q, int k, CancellationToken ct) { QueryCount++; return ValueTask.FromResult<IReadOnlyList<AgentMessage>>(QueryResult ?? []); }
    }

    [Fact]
    public async Task Append_fans_out_to_all_tiers()
    {
        var t1 = new SpyMemory();
        var t2 = new SpyMemory();
        var t3 = new SpyMemory();
        var c = new CompositeConversationMemory(t1, t2, t3);
        for (var i = 0; i < 3; i++)
            await c.AppendAsync(new AgentMessage("a", "r", i.ToString()), TestContext.Current.CancellationToken);
        t1.AppendCount.ShouldBe(3);
        t2.AppendCount.ShouldBe(3);
        t3.AppendCount.ShouldBe(3);
    }

    [Fact]
    public async Task GetRecent_defers_to_first_tier()
    {
        var t1 = new SpyMemory();
        var t2 = new SpyMemory();
        var c = new CompositeConversationMemory(t1, t2);
        await c.AppendAsync(new AgentMessage("a", "r", "x"), TestContext.Current.CancellationToken);
        await c.GetRecentAsync(5, TestContext.Current.CancellationToken);
        t1.RecentCount.ShouldBe(1);
        t2.RecentCount.ShouldBe(0);
    }

    [Fact]
    public async Task Query_merges_dedupes_trims()
    {
        var ts1 = new System.DateTimeOffset(2026, 4, 25, 1, 0, 0, System.TimeSpan.Zero);
        var ts2 = new System.DateTimeOffset(2026, 4, 25, 2, 0, 0, System.TimeSpan.Zero);
        var ts3 = new System.DateTimeOffset(2026, 4, 25, 3, 0, 0, System.TimeSpan.Zero);
        var t1 = new SpyMemory
        {
            QueryResult =
            [
                new AgentMessage("a", "r", "x") { Timestamp = ts1 },
                new AgentMessage("a", "r", "y") { Timestamp = ts2 },
            ],
        };
        var t2 = new SpyMemory
        {
            QueryResult =
            [
                new AgentMessage("a", "r", "x") { Timestamp = ts1 },  // duplicate (sender+ts)
                new AgentMessage("a", "r", "z") { Timestamp = ts3 },
            ],
        };
        var c = new CompositeConversationMemory(t1, t2);
        var q = await c.QueryAsync("any", topK: 2, TestContext.Current.CancellationToken);
        q.Count.ShouldBe(2);
        q[0].Content.ShouldBe("x");
        q[1].Content.ShouldBe("y");
    }

    [Fact]
    public void Empty_tiers_throws()
    {
        Should.Throw<System.ArgumentException>(() => new CompositeConversationMemory());
    }

    [Fact]
    public void Null_tiers_throws()
    {
        Should.Throw<System.ArgumentNullException>(() => new CompositeConversationMemory(null!));
    }
}
