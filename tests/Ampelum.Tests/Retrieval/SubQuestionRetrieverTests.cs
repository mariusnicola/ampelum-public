using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class SubQuestionRetrieverTests
{
    private sealed class FanoutInner : IRetriever
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
        {
            Calls.Add(q);
            return Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                [new RetrievedDocument(q, q, 1, new Dictionary<string, object?>())]);
        }
    }

    [Fact]
    public async Task Retrieve_FansOutSubQuestions_AndDedupes()
    {
        var inner = new FanoutInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("sub-q-a\nsub-q-b\nsub-q-a");
        var sut = new SubQuestionRetriever(inner, lm, new SubQuestionOptions());
        var r = await sut.RetrieveAsync("compound", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.ShouldContain("sub-q-a");
        inner.Calls.ShouldContain("sub-q-b");
        r.Select(d => d.Id).Distinct().Count().ShouldBe(r.Count);
    }

    [Fact]
    public async Task Retrieve_EmptyDecomposition_FallsBackToOriginal()
    {
        var inner = new FanoutInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("");
        var sut = new SubQuestionRetriever(inner, lm, new SubQuestionOptions());
        await sut.RetrieveAsync("orig", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.ShouldBe(["orig"]);
    }

    [Fact]
    public async Task Retrieve_TopK_Truncates()
    {
        var inner = new FanoutInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("a\nb\nc\nd");
        var sut = new SubQuestionRetriever(inner, lm, new SubQuestionOptions());
        var r = await sut.RetrieveAsync("q", 2, ct: TestContext.Current.CancellationToken);
        r.Count.ShouldBe(2);
    }

    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = [];
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"before:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add($"after:{ctx.ModuleName}");
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    [Fact]
    public async Task AmbientContext_RunsHooks_ButUsesInjectedChatClient()
    {
        var inner = new FanoutInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("only-sub");
        var decoyLm = new FakeChatClient().EnqueueText("decoy-sub");
        var hook = new SpyHook();
        var ambient = new AmpelumContext(decoyLm, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var sut = new SubQuestionRetriever(inner, lm, new SubQuestionOptions());

        using (AmpelumContext.Push(ambient))
        {
            await sut.RetrieveAsync("compound", 5, ct: TestContext.Current.CancellationToken);
        }

        inner.Calls.ShouldContain("only-sub");
        hook.Calls.ShouldBe(["before:SubQuestionRetriever.Decompose", "after:SubQuestionRetriever.Decompose"]);
        decoyLm.RecordedCalls.ShouldBeEmpty();
    }
}
