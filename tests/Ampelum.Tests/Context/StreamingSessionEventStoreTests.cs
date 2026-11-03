using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Context.Selection;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Context;

/// <summary>A reply the after hooks blocked must not stay in the event store, where a context selector would resend it.</summary>
public sealed class StreamingSessionEventStoreTests
{
    const string Bad = "BAD-REPLY";

    sealed class BadWordModerator : IOutputModerator
    {
        public ValueTask<ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken) =>
            new(new ModerationResult(text.Contains(Bad, StringComparison.Ordinal), [], null));
    }

    static async Task<List<EventEntry>> EventsAsync(InMemoryEventStore store)
    {
        var list = new List<EventEntry>();
        await foreach (var e in store.ReadAsync(new EventQuery(), TestContext.Current.CancellationToken)) list.Add(e);
        return list;
    }

    static (StreamingChatSession Session, InMemoryEventStore Store, FakeChatClient Chat) Build()
    {
        var chat = new FakeChatClient().EnqueueStreamingChunks(Bad).EnqueueStreamingChunks("a fine reply");
        var store = new InMemoryEventStore();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new ModerationHook(new BadWordModerator()));
        return (new StreamingChatSession(ctx, "Sys", eventStore: store, contextSelector: new TalkSelector()), store, chat);
    }

    [Fact]
    public async Task After_a_moderated_turn_the_store_holds_the_user_message_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, store, _) = Build();

        await Should.ThrowAsync<ModerationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("first question", ct)) { }
        });

        var events = await EventsAsync(store);
        events.Select(e => e.GetType().Name).ShouldBe(["UserMessage"]);
    }

    [Fact]
    public async Task The_next_turn_does_not_send_the_blocked_reply_to_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, store, chat) = Build();
        await Should.ThrowAsync<ModerationException>(async () =>
        {
            await foreach (var _ in session.SendAsync("first question", ct)) { }
        });

        await foreach (var _ in session.SendAsync("second question", ct)) { }

        var sent = string.Join("\n", chat.RecordedCalls[1].Messages.Select(m => m.Text));
        sent.ShouldContain("second question");
        sent.ShouldNotContain(Bad);
        (await EventsAsync(store)).OfType<AssistantText>().Select(a => a.Text).ShouldBe(["a fine reply"]);
    }

    [Fact]
    public async Task A_turn_that_passes_stores_its_reply()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new FakeChatClient().EnqueueStreamingChunks("fine");
        var store = new InMemoryEventStore();
        var ctx = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance)
            .WithHook(new ModerationHook(new BadWordModerator()));
        var session = new StreamingChatSession(ctx, "Sys", eventStore: store, contextSelector: new TalkSelector());

        await foreach (var _ in session.SendAsync("q", ct)) { }

        (await EventsAsync(store)).Select(e => e.GetType().Name).ShouldBe(["UserMessage", "AssistantText"]);
    }
}
