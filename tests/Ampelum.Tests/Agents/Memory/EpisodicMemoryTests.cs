using System.Threading.Tasks;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Agents.Memory;

public class EpisodicMemoryTests
{
    [Fact]
    public async Task Summarises_when_threshold_exceeded()
    {
        var chat = new FakeChatClient().EnqueueText("[[ ## Summary ## ]]\nepisode-1");
        var mem = new EpisodicMemory(chat, threshold: 10, windowSize: 10);
        for (var i = 0; i < 10; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", $"msg-{i}"), TestContext.Current.CancellationToken);
        // No summarisation yet
        var pre = await mem.GetRecentAsync(20, TestContext.Current.CancellationToken);
        pre.Count.ShouldBe(10);

        // 11th append triggers summarisation of the first 10
        await mem.AppendAsync(new AgentMessage("a", "r", "msg-10"), TestContext.Current.CancellationToken);
        var post = await mem.GetRecentAsync(20, TestContext.Current.CancellationToken);
        post.Count.ShouldBe(2);  // 1 summary + 1 newest
        post[0].SenderName.ShouldBe("episodic-summary");
        post[0].Content.ShouldBe("episode-1");
        post[1].Content.ShouldBe("msg-10");
    }

    [Fact]
    public async Task Continues_summarising_on_next_batch()
    {
        var chat = new FakeChatClient()
            .EnqueueText("[[ ## Summary ## ]]\nepisode-1")
            .EnqueueText("[[ ## Summary ## ]]\nepisode-2");
        var mem = new EpisodicMemory(chat, threshold: 10, windowSize: 10);
        for (var i = 0; i < 11; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", $"a{i}"), TestContext.Current.CancellationToken);
        // After 11: 1 summary + 1 = 2
        // Now add 10 more, count > threshold triggers another summarisation when count > 10
        for (var i = 0; i < 10; i++)
            await mem.AppendAsync(new AgentMessage("a", "r", $"b{i}"), TestContext.Current.CancellationToken);
        // After 11 appends: buffer = [summary1, msg10] (2). Then 10 b's appended one-at-a-time.
        // Trigger fires when count > 10. After b8 (count=11) it triggers and summarises first 10:
        // [summary1, msg10, b0..b7] -> summary2. Buffer = [summary2, b8]. b9 appended -> [summary2, b8, b9].
        var rec = await mem.GetRecentAsync(50, TestContext.Current.CancellationToken);
        rec.Count.ShouldBe(3);
        rec[0].SenderName.ShouldBe("episodic-summary");
        rec[0].Content.ShouldBe("episode-2");
        rec[1].Content.ShouldBe("b8");
        rec[2].Content.ShouldBe("b9");
    }

    [Fact]
    public async Task Query_falls_back_to_recent()
    {
        var chat = new FakeChatClient();
        var mem = new EpisodicMemory(chat, threshold: 10, windowSize: 10);
        await mem.AppendAsync(new AgentMessage("a", "r", "hi"), TestContext.Current.CancellationToken);
        var q = await mem.QueryAsync("anything", 5, TestContext.Current.CancellationToken);
        q.Count.ShouldBe(1);
    }

    [Fact]
    public void Null_chat_throws()
    {
        Should.Throw<System.ArgumentNullException>(() => new EpisodicMemory(null!));
    }

    [Fact]
    public void Threshold_must_be_positive()
    {
        var chat = new FakeChatClient();
        Should.Throw<System.ArgumentOutOfRangeException>(() => new EpisodicMemory(chat, threshold: 0));
    }

    [Fact]
    public void WindowSize_must_be_positive_and_le_threshold()
    {
        var chat = new FakeChatClient();
        Should.Throw<System.ArgumentOutOfRangeException>(() => new EpisodicMemory(chat, threshold: 5, windowSize: 0));
        Should.Throw<System.ArgumentOutOfRangeException>(() => new EpisodicMemory(chat, threshold: 5, windowSize: 6));
    }
}
