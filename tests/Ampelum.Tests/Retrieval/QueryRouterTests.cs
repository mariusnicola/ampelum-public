using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class QueryRouterTests
{
    private sealed class TaggedRetriever(string tag) : IRetriever
    {
        public Task<IReadOnlyList<RetrievedDocument>> RetrieveAsync(string q, int topK,
            IReadOnlyDictionary<string, object?>? f = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetrievedDocument>>(
                [new RetrievedDocument(tag, tag, 1, new Dictionary<string, object?>())]);
    }

    [Fact]
    public async Task Routes_BasedOnLmDecision()
    {
        var lm = new FakeChatClient();
        lm.EnqueueText("docs");
        var sut = new QueryRouter(lm, new QueryRouterOptions
        {
            Routes = new Dictionary<string, IRetriever>
            {
                ["docs"] = new TaggedRetriever("docs-result"),
                ["code"] = new TaggedRetriever("code-result"),
            },
            SystemPrompt = "Pick docs or code.",
        });
        var r = await sut.RetrieveAsync("how do I", 1, ct: TestContext.Current.CancellationToken);
        r[0].Id.ShouldBe("docs-result");
    }

    [Fact]
    public async Task UnknownRoute_FallsBackToDefault()
    {
        var lm = new FakeChatClient();
        lm.EnqueueText("nonexistent");
        var sut = new QueryRouter(lm, new QueryRouterOptions
        {
            Routes = new Dictionary<string, IRetriever>
            {
                ["docs"] = new TaggedRetriever("docs"),
                ["default"] = new TaggedRetriever("default"),
            },
            SystemPrompt = "x",
        });
        var r = await sut.RetrieveAsync("q", 1, ct: TestContext.Current.CancellationToken);
        r[0].Id.ShouldBe("default");
    }

    [Fact]
    public async Task UnknownRoute_NoDefault_FallsToFirst()
    {
        var lm = new FakeChatClient();
        lm.EnqueueText("nope");
        var sut = new QueryRouter(lm, new QueryRouterOptions
        {
            Routes = new Dictionary<string, IRetriever>
            {
                ["only"] = new TaggedRetriever("only"),
            },
            SystemPrompt = "x",
        });
        var r = await sut.RetrieveAsync("q", 1, ct: TestContext.Current.CancellationToken);
        r[0].Id.ShouldBe("only");
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
        var lm = new FakeChatClient();
        lm.EnqueueText("docs");
        var decoyLm = new FakeChatClient().EnqueueText("code");
        var hook = new SpyHook();
        var ambient = new AmpelumContext(decoyLm, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };
        var sut = new QueryRouter(lm, new QueryRouterOptions
        {
            Routes = new Dictionary<string, IRetriever>
            {
                ["docs"] = new TaggedRetriever("docs-result"),
                ["code"] = new TaggedRetriever("code-result"),
            },
            SystemPrompt = "Pick docs or code.",
        });

        using (AmpelumContext.Push(ambient))
        {
            var r = await sut.RetrieveAsync("how do I", 1, ct: TestContext.Current.CancellationToken);
            r[0].Id.ShouldBe("docs-result");
        }

        hook.Calls.ShouldBe(["before:QueryRouter.Route", "after:QueryRouter.Route"]);
        decoyLm.RecordedCalls.ShouldBeEmpty();
    }
}
