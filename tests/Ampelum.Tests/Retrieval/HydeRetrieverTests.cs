using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class HydeRetrieverTests
{
    private sealed class StubInner : IRetriever
    {
        public string? LastQuery;
        public IReadOnlyDictionary<string, object?>? LastFilters;
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
        {
            LastQuery = q;
            LastFilters = f;
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                [new RetrievedDocument("d", "doc text", 1.0, new Dictionary<string, object?>())]);
        }
    }

    [Fact]
    public async Task Retrieve_GeneratesHypothesis_AndQueriesInnerWithIt()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("HYPOTHETICAL DOC ABOUT TOPIC");
        var sut = new HydeRetriever(inner, lm, new HydeOptions());
        var r = await sut.RetrieveAsync("real question?", 1, ct: TestContext.Current.CancellationToken);
        inner.LastQuery.ShouldBe("HYPOTHETICAL DOC ABOUT TOPIC");
        r.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Retrieve_Disabled_FallsThroughToOriginalQuery()
    {
        var inner = new StubInner();
        var sut = new HydeRetriever(inner, new FakeChatClient(), new HydeOptions { IsEnabled = false });
        await sut.RetrieveAsync("original q", 1, ct: TestContext.Current.CancellationToken);
        inner.LastQuery.ShouldBe("original q");
    }

    [Fact]
    public async Task Retrieve_PassesFiltersThrough()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("hyp");
        var sut = new HydeRetriever(inner, lm, new HydeOptions());
        var f = new Dictionary<string, object?> { ["k"] = "v" };
        await sut.RetrieveAsync("q", 1, f, TestContext.Current.CancellationToken);
        inner.LastFilters.ShouldBe(f);
    }

    [Fact]
    public async Task Retrieve_RespectsCustomChatOptions()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("h");
        var sut = new HydeRetriever(inner, lm, new HydeOptions { ChatOptions = new Microsoft.Extensions.AI.ChatOptions { Temperature = 0.5f } });
        await sut.RetrieveAsync("q", 1, ct: TestContext.Current.CancellationToken);
        inner.LastQuery.ShouldBe("h");
    }
}
