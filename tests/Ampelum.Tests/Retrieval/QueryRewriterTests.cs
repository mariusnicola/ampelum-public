using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Retrieval.Patterns;
using SignalGrid.Ampelum.Testing;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Retrieval;

public sealed class QueryRewriterTests
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

    [Fact]
    public async Task Rewrite_ReturnsTrimmedLmResponse()
    {
        var lm = new FakeChatClient();
        lm.EnqueueText("  rewritten query  \n");
        var sut = new QueryRewriter(lm, "Rewrite for clarity.");
        var r = await sut.RewriteAsync("orig", TestContext.Current.CancellationToken);
        r.ShouldBe("rewritten query");
    }

    [Fact]
    public async Task Rewrite_WithAmbientContext_RunsHooksAndUsesInjectedChat_NotAmbientChat()
    {
        var injectedChat = new FakeChatClient().EnqueueText("rewritten via injected");
        var ambientChat = new FakeChatClient(); // nothing enqueued — must not be called
        var hook = new SpyHook();
        var ambientContext = new AmpelumContext(ambientChat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = [hook],
        };

        using var _ = AmpelumContext.Push(ambientContext);
        var sut = new QueryRewriter(injectedChat, "Rewrite for clarity.");
        var r = await sut.RewriteAsync("orig", TestContext.Current.CancellationToken);

        r.ShouldBe("rewritten via injected");
        injectedChat.RecordedCalls.Count.ShouldBe(1);
        ambientChat.RecordedCalls.ShouldBeEmpty();
        hook.Calls.ShouldBe(["before:QueryRewriter.Rewrite", "after:QueryRewriter.Rewrite"]);
    }
}
