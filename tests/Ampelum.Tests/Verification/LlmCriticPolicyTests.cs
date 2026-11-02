using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Verification;

public sealed class LlmCriticPolicyTests
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

        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct)
        {
            Calls.Add("error");
            return default;
        }
    }

    static AmpelumContext Ctx(IChatClient chat, params IHook[] hooks) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
        {
            Hooks = hooks.Length == 0 ? null : hooks,
        };

    static Example MakeExample(string key, string value) =>
        new(new Dictionary<string, object?> { [key] = value }, new Dictionary<string, object?>());

    [Fact]
    public async Task EvaluateAsync_UsesContextChat_FiresHooks_ApprovedVerdict()
    {
        var chat = new FakeChatClient().EnqueueText("APPROVED: looks good");
        var hook = new SpyHook();
        var context = Ctx(chat, hook);
        var policy = new LlmCriticPolicy<object>();

        var verdict = await policy.EvaluateAsync(
            MakeExample("q", "2+2"), MakeExample("a", "4"), context, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeTrue();
        verdict.Reasoning.ShouldBe("looks good");
        hook.Calls.ShouldBe(new[] { "before", "after" });
        chat.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_RejectedVerdict_IncludesSuggestions()
    {
        var chat = new FakeChatClient().EnqueueText("REJECTED: wrong answer");
        var context = Ctx(chat);
        var policy = new LlmCriticPolicy<object>();

        var verdict = await policy.EvaluateAsync(
            MakeExample("q", "2+2"), MakeExample("a", "5"), context, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeFalse();
        verdict.Reasoning.ShouldBe("wrong answer");
        verdict.RejectionReason.ShouldBe(CriticRejectionReason.QualityBelowThreshold);
        verdict.Suggestions.ShouldNotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_WithDedicatedCriticClient_UsesCriticClient_NotContextChat_ButStillFiresContextHooks()
    {
        // _criticClient differs from context.Chat: the call must go to the dedicated client,
        // while the hook pipeline (which lives on `context`, not on the client) still fires.
        var criticClient = new FakeChatClient().EnqueueText("APPROVED: from dedicated client");
        var contextChat = new FakeChatClient(); // nothing enqueued — must NOT be called
        var hook = new SpyHook();
        var context = Ctx(contextChat, hook);
        var policy = new LlmCriticPolicy<object>(criticClient);

        var verdict = await policy.EvaluateAsync(
            MakeExample("q", "x"), MakeExample("a", "y"), context, TestContext.Current.CancellationToken);

        verdict.Approved.ShouldBeTrue();
        verdict.Reasoning.ShouldBe("from dedicated client");
        hook.Calls.ShouldBe(new[] { "before", "after" });
        criticClient.RecordedCalls.Count.ShouldBe(1);
        contextChat.RecordedCalls.ShouldBeEmpty();
    }
}
