using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AmpelumContextRetrieverTests
{
    static AmpelumContext MakeBase() => new(
        new FakeChatClient(),
        new ChatAdapter(),
        NullTraceSink.Instance,
        NullLoggerFactory.Instance);

    [Fact]
    public void Retriever_DefaultsToNull()
    {
        var ctx = MakeBase();
        ctx.Retriever.ShouldBeNull();
    }

    [Fact]
    public void Retriever_CanBeSetViaInitProperty()
    {
        var fakeRetriever = new StubRetriever();
        var ctx = MakeBase() with { Retriever = fakeRetriever };
        ctx.Retriever.ShouldBeSameAs(fakeRetriever);
    }

    [Fact]
    public void Ctor_FourArgs_StillCompiles_NonBreaking()
    {
        var ctx = new AmpelumContext(
            new FakeChatClient(),
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        ctx.Retriever.ShouldBeNull();
    }

    sealed class StubRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(
            string query, int topK,
            IReadOnlyDictionary<string, object?>? filters = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>([]);
    }
}
