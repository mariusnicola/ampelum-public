using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextInspectHistoryTests
{
    static TraceRecord Rec(string module, string raw, long? ti, long? to) => new(
        Guid.NewGuid(), module, typeof(object),
        new Dictionary<string, object?>(), new Dictionary<string, object?>(),
        new[] { new ChatMessage(ChatRole.User, "ask") },
        raw, ti is null ? null : new UsageDetails { InputTokenCount = ti, OutputTokenCount = to },
        TimeSpan.FromMilliseconds(42), null, new Dictionary<string, object?>());

    [Fact]
    public void InspectHistory_NonInspectableSink_ReturnsEmptyString()
    {
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(),
            NullTraceSink.Instance, NullLoggerFactory.Instance);
        ctx.InspectHistory().ShouldBe(string.Empty);
    }

    [Fact]
    public void InspectHistory_Zero_ReturnsEmpty()
    {
        var sink = new InMemoryTraceSink();
        sink.Write(Rec("Predict", "raw", 1, 2));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        ctx.InspectHistory(0).ShouldBe(string.Empty);
    }

    [Fact]
    public void InspectHistory_One_RendersLastRecord()
    {
        var sink = new InMemoryTraceSink();
        sink.Write(Rec("Predict", "raw-a", 10, 5));
        sink.Write(Rec("ChainOfThought", "raw-b", 20, 10));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var s = ctx.InspectHistory(1);
        s.ShouldContain("ChainOfThought");
        s.ShouldContain("prompt_tokens=20");
        s.ShouldContain("completion_tokens=10");
        s.ShouldContain("--- PROMPT ---");
        s.ShouldContain("--- RESPONSE ---");
        s.ShouldContain("raw-b");
    }

    [Fact]
    public void InspectHistory_N_gt_available_ReturnsAll()
    {
        var sink = new InMemoryTraceSink();
        sink.Write(Rec("Predict", "raw", null, null));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        var s = ctx.InspectHistory(99);
        s.ShouldContain("Predict");
    }

    [Fact]
    public void InspectHistory_EmptySink_ReturnsEmpty()
    {
        var sink = new InMemoryTraceSink();
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        ctx.InspectHistory(1).ShouldBe(string.Empty);
    }

    [Fact]
    public void InspectHistory_Negative_ReturnsEmpty()
    {
        var sink = new InMemoryTraceSink();
        sink.Write(Rec("Predict", "raw", 1, 2));
        var ctx = new AmpelumContext(new FakeChatClient(), new ChatAdapter(), sink, NullLoggerFactory.Instance);
        ctx.InspectHistory(-1).ShouldBe(string.Empty);
    }
}
