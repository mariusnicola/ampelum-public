using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class RagAdvisorTests
{
    sealed class SpyHook : IHook
    {
        public int Order => 0;
        public List<string> Calls { get; } = new();

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add("before");
            return default;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            Calls.Add("after");
            return default;
        }

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    private sealed class FixedRetriever(params (string id, string text)[] docs) : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                docs.Take(topK).Select(d => new RetrievedDocument(d.id, d.text, 1, new Dictionary<string, object?>())).ToList());
    }

    [Fact]
    public async Task AskAsync_ReturnsAnswer_AndCitations()
    {
        var inner = new FixedRetriever(("d1", "Paris is the capital of France."));
        var lm = new FakeChatClient();
        lm.EnqueueText("Paris [doc1]");
        var sut = new RagAdvisor(inner, lm, new RagAdvisorOptions());
        var r = await sut.AskAsync("Capital of France?", TestContext.Current.CancellationToken);
        r.Answer.ShouldContain("Paris");
        r.Citations.Single().Id.ShouldBe("d1");
    }

    [Fact]
    public async Task AskAsync_RespectsTopK()
    {
        var inner = new FixedRetriever(("a", "x"), ("b", "y"), ("c", "z"));
        var lm = new FakeChatClient();
        lm.EnqueueText("answer");
        var sut = new RagAdvisor(inner, lm, new RagAdvisorOptions { TopK = 2 });
        var r = await sut.AskAsync("q", TestContext.Current.CancellationToken);
        r.Citations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AskAsync_WithAmbientContext_RoutesThroughHooks_UsesCtorSuppliedClient()
    {
        var inner = new FixedRetriever(("d1", "Paris is the capital of France."));
        var lm = new FakeChatClient();
        lm.EnqueueText("Paris [doc1]");
        var ambientChat = new FakeChatClient(); // must NOT be called — RagAdvisor's own `lm` client must answer
        var hook = new SpyHook();
        var ambient = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var sut = new RagAdvisor(inner, lm, new RagAdvisorOptions());

        using (AmpelumContext.Push(ambient))
        {
            var r = await sut.AskAsync("Capital of France?", TestContext.Current.CancellationToken);
            r.Answer.ShouldContain("Paris");
        }

        hook.Calls.ShouldBe(new[] { "before", "after" });
        lm.RecordedCalls.Count.ShouldBe(1);
        ambientChat.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AskAsync_NoAmbientContext_CallsChatDirectly_Unchanged()
    {
        // Backward-compat: RagAdvisor has no ctor-injected AmpelumContext, so with no ambient
        // scope pushed the call must behave exactly as before (no hooks, direct client call).
        var inner = new FixedRetriever(("d1", "text"));
        var lm = new FakeChatClient();
        lm.EnqueueText("answer");
        var sut = new RagAdvisor(inner, lm, new RagAdvisorOptions());

        var r = await sut.AskAsync("q", TestContext.Current.CancellationToken);

        r.Answer.ShouldBe("answer");
        lm.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public void Options_DefaultsApplied()
    {
        var o = new RagAdvisorOptions();
        o.TopK.ShouldBe(5);
        o.ContextSeparator.ShouldContain("---");
    }
}
