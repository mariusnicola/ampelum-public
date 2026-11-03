using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Caching;
using SignalGrid.Ampelum.Caching.Semantic;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Safety.Policy;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Caching.Semantic;

/// <summary>A reply a hook blocked is never stored or served as an answer, and a cached raw model answer is moderated again on every serve.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SemanticCacheBlockedReplyTests
{
    const string Bad = "BAD-REPLY";

    [Signature("Answer the question.")]
    public sealed record Ask : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class BadWordModerator : IOutputModerator
    {
        public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken) =>
            new(new ModerationResult(text.Contains(Bad, StringComparison.Ordinal), [], null));
    }

    static string Reply(string a) => "[[ ## Answer ## ]]\n" + a + "\n[[ ## completed ## ]]";

    static AmpelumContext Context(FakeChatClient chat, params IHook[] hooks)
    {
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        foreach (var h in hooks) ctx = ctx.WithHook(h);
        return ctx;
    }

    [Fact]
    public async Task A_moderated_reply_is_not_cached_so_the_next_identical_question_reaches_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var chat = new FakeChatClient().EnqueueText(Reply(Bad)).EnqueueText(Reply("a clean answer"));
        var predict = new Predict<Ask>(Context(chat, new SemanticCacheHook(cache), new ModerationHook(new BadWordModerator())));

        await Should.ThrowAsync<ModerationException>(() => predict.ForwardAsync(new Ask { Question = "same" }, ct: ct));
        cache.Count.ShouldBe(0);
        var second = await predict.ForwardAsync(new Ask { Question = "same" }, ct: ct);

        chat.RecordedCalls.Count.ShouldBe(2);
        second.Value.Answer.ShouldBe("a clean answer");
    }

    [Fact]
    public async Task A_content_policy_rejected_reply_is_not_cached()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new InMemorySemanticCache();
        var chat = new FakeChatClient().EnqueueText(Reply(Bad)).EnqueueText(Reply("a clean answer"));
        var policy = new ContentPolicyBuilder().Reject("bad", new Regex(Bad)).Build();
        var predict = new Predict<Ask>(Context(chat, new SemanticCacheHook(cache), new ContentPolicyHook(policy)));

        await Should.ThrowAsync<ContentPolicyViolationException>(() => predict.ForwardAsync(new Ask { Question = "same" }, ct: ct));
        var second = await predict.ForwardAsync(new Ask { Question = "same" }, ct: ct);

        cache.Count.ShouldBe(1);
        second.Value.Answer.ShouldBe("a clean answer");
    }

    sealed class EmbeddingCounter : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0 }))));
        }
    }

    [Fact]
    public async Task A_blocked_reply_is_not_embedded_either()
    {
        var ct = TestContext.Current.CancellationToken;
        var embeddings = new EmbeddingCounter();
        var chat = new FakeChatClient().EnqueueText(Reply(Bad));
        var predict = new Predict<Ask>(Context(chat,
            new SemanticCacheHook(new InMemorySemanticCache(), embeddings), new ModerationHook(new BadWordModerator())));

        await Should.ThrowAsync<ModerationException>(() => predict.ForwardAsync(new Ask { Question = "q" }, ct: ct));

        embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_raw_answer_cached_below_the_hooks_is_moderated_again_on_every_serve()
    {
        var ct = TestContext.Current.CancellationToken;
        using var tokenCache = new TokenCache();
        var chat = new FakeChatClient().EnqueueText(Reply(Bad)).EnqueueText(Reply("unused"));
        var cached = new TokenCachedChatClient(chat, tokenCache);
        var predict = new Predict<Ask>(Context2(cached, new ModerationHook(new BadWordModerator())));

        await Should.ThrowAsync<ModerationException>(() => predict.ForwardAsync(new Ask { Question = "same" }, ct: ct));
        await Should.ThrowAsync<ModerationException>(() => predict.ForwardAsync(new Ask { Question = "same" }, ct: ct));

        // The second call was served from the token cache (one model call), and the hooks moderated it again.
        chat.RecordedCalls.Count.ShouldBe(1);
        tokenCache.Count.ShouldBe(1);
    }

    static AmpelumContext Context2(IChatClient chat, params IHook[] hooks)
    {
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        foreach (var h in hooks) ctx = ctx.WithHook(h);
        return ctx;
    }
}
