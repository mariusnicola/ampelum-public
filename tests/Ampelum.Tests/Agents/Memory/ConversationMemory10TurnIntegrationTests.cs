using System.Collections.Generic;
using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Memory.Integration;

public class ConversationMemory10TurnIntegrationTests
{
    [Fact]
    public async Task Composite_memory_across_12_turns_with_summary_and_query()
    {
        // Vector tier: deterministic embeddings keyed on substring.
        var scripted = new Dictionary<string, float[]>
        {
            ["apple-1"] = [1f, 0f],
            ["apple-2"] = [1f, 0f],
            ["apple-3"] = [1f, 0f],
            ["apple-4"] = [1f, 0f],
            ["rocket-1"] = [0f, 1f],
            ["rocket-2"] = [0f, 1f],
            ["rocket-3"] = [0f, 1f],
            ["rocket-4"] = [0f, 1f],
            ["rocket-5"] = [0f, 1f],
            ["rocket-6"] = [0f, 1f],
            ["rocket-7"] = [0f, 1f],
            ["rocket-8"] = [0f, 1f],
            ["rocket telemetry"] = [0f, 1f],  // query string
        };
        var emb = new FakeEmbeddingGenerator(scripted, fallbackDimensions: 2);

        var inMem = new InMemoryConversationMemory(capacity: 20);
        var vec = new VectorConversationMemory(new InMemoryVectorStore(), emb);

        var summarizerChat = new FakeChatClient().EnqueueText("[[ ## Summary ## ]]\nfirst-10-summarised");
        var ep = new EpisodicMemory(summarizerChat, threshold: 10, windowSize: 10);

        var composite = new CompositeConversationMemory(inMem, vec, ep);

        // Append 4 apple turns + 8 rocket turns = 12 turns.
        var contents = new[]
        {
            "apple-1", "apple-2", "apple-3", "apple-4",
            "rocket-1", "rocket-2", "rocket-3", "rocket-4",
            "rocket-5", "rocket-6", "rocket-7", "rocket-8",
        };
        foreach (var c in contents)
            await composite.AppendAsync(new AgentMessage("a", "r", c), TestContext.Current.CancellationToken);

        // Episodic tier should have summarised the first 10 after the 11th append.
        var epRecent = await ep.GetRecentAsync(50, TestContext.Current.CancellationToken);
        epRecent[0].SenderName.ShouldBe("episodic-summary");
        epRecent[0].Content.ShouldBe("first-10-summarised");

        // Composite query for rocket telemetry must surface rocket messages from the vector tier.
        var top = await composite.QueryAsync("rocket telemetry", topK: 3, TestContext.Current.CancellationToken);
        top.Count.ShouldBeGreaterThan(0);
        foreach (var m in top)
            m.Content.ShouldContain("rocket");

        // GetRecent defers to the first tier (in-memory ring buffer) and returns the 5 most-recent appends.
        var recent5 = await composite.GetRecentAsync(5, TestContext.Current.CancellationToken);
        recent5.Count.ShouldBe(5);
        recent5[0].Content.ShouldBe("rocket-4");
        recent5[4].Content.ShouldBe("rocket-8");
    }
}
