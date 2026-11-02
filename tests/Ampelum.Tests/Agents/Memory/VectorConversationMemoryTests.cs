using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Memory;

public class VectorConversationMemoryTests
{
    static (FakeEmbeddingGenerator emb, ReadOnlyMemory<float> appleQuery, ReadOnlyMemory<float> rocketQuery) MakeFake()
    {
        // Pre-canned vectors keyed on substring. apple ~ [1,0], rocket ~ [0,1]
        var scripted = new Dictionary<string, float[]>(System.StringComparer.Ordinal)
        {
            ["apples are tasty"] = [1f, 0f],
            ["apple farming"] = [1f, 0f],
            ["I love apples"] = [1f, 0f],
            ["rockets are loud"] = [0f, 1f],
            ["rocket telemetry"] = [0f, 1f],
            ["I love rockets"] = [0f, 1f],
        };
        var emb = new FakeEmbeddingGenerator(scripted, fallbackDimensions: 2);
        return (emb, new float[] { 1f, 0f }, new float[] { 0f, 1f });
    }

    [Fact]
    public async Task Query_returns_semantically_close_messages()
    {
        var (emb, _, _) = MakeFake();
        var store = new InMemoryVectorStore();
        var mem = new VectorConversationMemory(store, emb);

        await mem.AppendAsync(new AgentMessage("a", "r", "apples are tasty"), TestContext.Current.CancellationToken);
        await mem.AppendAsync(new AgentMessage("a", "r", "rockets are loud"), TestContext.Current.CancellationToken);
        await mem.AppendAsync(new AgentMessage("a", "r", "I love apples"), TestContext.Current.CancellationToken);
        await mem.AppendAsync(new AgentMessage("a", "r", "I love rockets"), TestContext.Current.CancellationToken);

        var top = await mem.QueryAsync("apple farming", topK: 2, TestContext.Current.CancellationToken);
        top.Count.ShouldBe(2);
        top.ShouldAllBe(m => m.Content.Contains("apple"));
    }

    [Fact]
    public async Task GetRecent_returns_chronological_tail()
    {
        var (emb, _, _) = MakeFake();
        var store = new InMemoryVectorStore();
        var mem = new VectorConversationMemory(store, emb);

        for (var i = 0; i < 4; i++)
        {
            var content = i % 2 == 0 ? "apples are tasty" : "rockets are loud";
            await mem.AppendAsync(new AgentMessage("a", "r", content + " " + i), TestContext.Current.CancellationToken);
        }
        var recent = await mem.GetRecentAsync(2, TestContext.Current.CancellationToken);
        recent.Count.ShouldBe(2);
        recent[0].Content.ShouldEndWith("2");
        recent[1].Content.ShouldEndWith("3");
    }

    [Fact]
    public async Task GetRecent_zero_returns_empty()
    {
        var (emb, _, _) = MakeFake();
        var mem = new VectorConversationMemory(new InMemoryVectorStore(), emb);
        await mem.AppendAsync(new AgentMessage("a", "r", "apples are tasty"), TestContext.Current.CancellationToken);
        var recent = await mem.GetRecentAsync(0, TestContext.Current.CancellationToken);
        recent.Count.ShouldBe(0);
    }

    [Fact]
    public void Null_store_throws()
    {
        var (emb, _, _) = MakeFake();
        Should.Throw<System.ArgumentNullException>(() => new VectorConversationMemory(null!, emb));
    }

    [Fact]
    public void Null_embedder_throws()
    {
        Should.Throw<System.ArgumentNullException>(() => new VectorConversationMemory(new InMemoryVectorStore(), null!));
    }
}
