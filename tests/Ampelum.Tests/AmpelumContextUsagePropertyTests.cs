using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using SignalGrid.Ampelum.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextUsagePropertyTests
{
    static AmpelumContext Basic() => new(
        new FakeChatClient(), new StubAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    [Fact]
    public void Usage_IsNull_ByDefault() =>
        Basic().Usage.ShouldBeNull();

    [Fact]
    public void Usage_CanBeSet_ViaWithExpression()
    {
        var tracker = new DefaultUsageTracker();
        var ctx = Basic() with { Usage = tracker };
        ctx.Usage.ShouldBeSameAs(tracker);
    }

    sealed class StubAdapter : IAdapter
    {
        public IReadOnlyList<ChatMessage> Format(SignatureMetadata s, IReadOnlyDictionary<string, object?> i, IReadOnlyList<Example> d) => [];
        public IReadOnlyDictionary<string, object?> Parse(SignatureMetadata s, ChatResponse r) => new Dictionary<string, object?>();
    }
}
