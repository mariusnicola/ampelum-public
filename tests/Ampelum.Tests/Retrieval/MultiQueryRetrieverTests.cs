using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class MultiQueryRetrieverTests
{
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
    public async Task Retrieve_FansOutOriginalAndParaphrases_FusesByRRF()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("paraphrase-1\nparaphrase-2");
        var sut = new MultiQueryRetriever(inner, lm, new MultiQueryOptions { NumParaphrases = 2 });
        var r = await sut.RetrieveAsync("orig", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.ShouldContain("orig");
        inner.Calls.ShouldContain("paraphrase-1");
        inner.Calls.ShouldContain("paraphrase-2");
        r.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Retrieve_DedupesParaphrases()
    {
        var inner = new StubInner();
        var lm = new FakeChatClient();
        lm.EnqueueText("orig\norig"); // duplicates of original
        var sut = new MultiQueryRetriever(inner, lm, new MultiQueryOptions { NumParaphrases = 2 });
        await sut.RetrieveAsync("orig", 5, ct: TestContext.Current.CancellationToken);
        inner.Calls.Count(c => c == "orig").ShouldBe(1);
    }

    [Fact]
    public async Task Retrieve_WithAmbientContext_RunsHooksAndUsesInjectedChat_NotAmbientChat()
    {
        var inner = new StubInner();
        var injectedChat = new FakeChatClient().EnqueueText("paraphrase-1");
        var ambientChat = new FakeChatClient(); // nothing enqueued — must not be called
        var hook = new SpyHook();
        var ambientContext = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        using var _ = AmpelumContext.Push(ambientContext);
        var sut = new MultiQueryRetriever(inner, injectedChat, new MultiQueryOptions { NumParaphrases = 1 });
        var r = await sut.RetrieveAsync("orig", 5, ct: TestContext.Current.CancellationToken);

        r.Count.ShouldBe(2);
        injectedChat.RecordedCalls.Count.ShouldBe(1);
        ambientChat.RecordedCalls.ShouldBeEmpty();
        hook.Calls.ShouldBe(["before:MultiQueryRetriever.Paraphrase", "after:MultiQueryRetriever.Paraphrase"]);
    }
}
