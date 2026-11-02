using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class StepBackRetrieverTests
{
    private sealed class StubInner : IRetriever
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
        {
            Calls.Add(q);
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                [new RetrievedDocument($"d-{q}", q, 1, new Dictionary<string, object?>())]);
        }
    }

    [Fact]
    public async Task Retrieve_QueriesBothOriginalAndStepBack()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("abstract version");
        var sut = new StepBackRetriever(inner, lm, new StepBackOptions());
        await sut.RetrieveAsync("specific question", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.ShouldContain("specific question");
        inner.Calls.ShouldContain("abstract version");
    }

    [Fact]
    public async Task Retrieve_IncludeOriginalFalse_OnlyStepBack()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("abstract");
        var sut = new StepBackRetriever(inner, lm, new StepBackOptions { IncludeOriginal = false });
        await sut.RetrieveAsync("orig", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.ShouldBe(["abstract"]);
    }
}
