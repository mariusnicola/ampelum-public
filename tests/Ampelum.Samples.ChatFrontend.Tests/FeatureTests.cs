using System.Collections.Concurrent;
using System.Diagnostics;
using Shouldly;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.ChatFrontend.Tests;

public sealed class FeatureTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Conversation_DeliversTheReplyTokenByToken()
    {
        using var h = new Harness();
        h.Primary.EnqueueStreamingChunks("Hel", "lo ", "there");

        var tokens = new List<string>();
        await foreach (var token in h.Backend.OpenConversation().SendAsync("Hi", Ct))
            tokens.Add(token);

        tokens.ShouldBe(["Hel", "lo ", "there"]);
        h.Primary.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Conversation_SendsTheSystemPromptAndEarlierTurnsWithEachMessage()
    {
        using var h = new Harness();
        h.Primary.EnqueueStreamingChunks("One").EnqueueStreamingChunks("Two");
        var conversation = h.Backend.OpenConversation();

        await foreach (var _ in conversation.SendAsync("first", Ct)) { }
        await foreach (var _ in conversation.SendAsync("second", Ct)) { }

        var secondCall = h.Primary.RecordedCalls[1].Messages.Select(m => m.Text).ToList();
        secondCall.ShouldBe([ChatBackend.SystemPrompt, "first", "One", "second"]);
    }

    [Fact]
    public async Task FieldStream_YieldsEachTypedFieldThenOneFinalUpdateWithTheTypedValue()
    {
        using var h = new Harness();
        h.Primary.EnqueueStreamingOutputs(Harness.Reply("Within 30 days.", "Need a label?"));

        var updates = new List<SignalGrid.Ampelum.Streaming.StreamUpdate<ReplySignature>>();
        await foreach (var update in h.Backend.StreamFieldsAsync("Returns?", Ct))
            updates.Add(update);

        updates.Select(u => u.FieldName).Distinct().ShouldBe(["Answer", "FollowUp"]);
        updates.Count(u => u.IsFinal).ShouldBe(1);
        var final = updates[^1];
        final.IsFinal.ShouldBeTrue();
        final.FinalValue.ShouldNotBeNull();
        final.FinalValue.Answer.ShouldBe("Within 30 days.");
        final.FinalValue.FollowUp.ShouldBe("Need a label?");
        updates.Take(updates.Count - 1).ShouldAllBe(u => u.FinalValue == null);
        updates.First(u => u.FieldName == "Answer").PartialText.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task FieldStream_RunsThroughProgramStreamer_WhichRecordsTheStreamedFieldOnItsSpan()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SignalGrid.Ampelum",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var h = new Harness();
        h.Primary.EnqueueStreamingOutputs(Harness.Reply("Within 30 days."));

        await foreach (var _ in h.Backend.StreamFieldsAsync("Returns span?", Ct)) { }

        var span = spans.Single(a => a.OperationName == "SignalGrid.Ampelum.program.stream"
            && a.GetTagItem("program.type") as string == nameof(ReplyProgram));
        span.GetTagItem("stream.field_name").ShouldBe(ReplyProgram.PrimaryField);
    }

    [Fact]
    public async Task SemanticCache_AnswersARepeatedQuestionWithoutAModelCall()
    {
        using var h = new Harness();
        h.Primary.EnqueueOutputs(Harness.Reply("We ship to all EU countries."));

        var first = await h.Backend.AskAsync("Do you ship to Germany?", Ct);
        var second = await h.Backend.AskAsync("Do you ship to Germany?", Ct);

        second.Answer.ShouldBe(first.Answer);
        second.FollowUp.ShouldBe(first.FollowUp);
        h.Primary.RecordedCalls.Count.ShouldBe(1);
        h.Backend.CachedAnswers.ShouldBe(1);
    }

    [Fact]
    public async Task SemanticCache_AnotherQuestionStillReachesTheModel()
    {
        using var h = new Harness();
        h.Primary.EnqueueOutputs(Harness.Reply("Yes, Germany."));
        h.Primary.EnqueueOutputs(Harness.Reply("Yes, France."));

        var germany = await h.Backend.AskAsync("Do you ship to Germany?", Ct);
        var france = await h.Backend.AskAsync("Do you ship to France?", Ct);

        germany.Answer.ShouldBe("Yes, Germany.");
        france.Answer.ShouldBe("Yes, France.");
        h.Primary.RecordedCalls.Count.ShouldBe(2);
        h.Backend.CachedAnswers.ShouldBe(2);
    }

    [Fact]
    public async Task TokenCache_StopsARepeatedIdenticalPromptFromReachingTheModel()
    {
        using var h = new Harness();
        h.Primary.EnqueueText("9 to 17.");

        var first = await h.Backend.QuickReplyAsync("Opening hours?", Ct);
        var second = await h.Backend.QuickReplyAsync("Opening hours?", Ct);

        second.Text.ShouldBe(first.Text);
        h.Primary.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TokenCache_ADifferentPromptStillReachesTheModel()
    {
        using var h = new Harness();
        h.Primary.EnqueueText("9 to 17.").EnqueueText("Closed on Sunday.");

        var weekdays = await h.Backend.QuickReplyAsync("Opening hours?", Ct);
        var sunday = await h.Backend.QuickReplyAsync("Open on Sunday?", Ct);

        weekdays.Text.ShouldBe("9 to 17.");
        sunday.Text.ShouldBe("Closed on Sunday.");
        h.Primary.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failover_WithAHealthyPrimary_NeverCallsTheFallback()
    {
        using var h = new Harness();
        h.Primary.EnqueueText("From the primary.");

        var reply = await h.Backend.QuickReplyAsync("Hello?", Ct);

        reply.ShouldBe(new ChatReply("From the primary.", ChatBackend.PrimaryEndpoint));
        h.Primary.RecordedCalls.Single().Options!.ModelId.ShouldBe(Harness.PrimaryId);
        h.Fallback.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failover_WhenThePrimaryFails_TheFallbackModelAnswers()
    {
        using var h = new Harness();
        h.Primary.EnqueueError(new HttpRequestException("primary is down"));
        h.Fallback.EnqueueText("From the fallback.");

        var reply = await h.Backend.QuickReplyAsync("Hello?", Ct);

        reply.ShouldBe(new ChatReply("From the fallback.", ChatBackend.FallbackEndpoint));
        h.Primary.RecordedCalls.Single().Options!.ModelId.ShouldBe(Harness.PrimaryId);
        h.Fallback.RecordedCalls.Single().Options!.ModelId.ShouldBe(Harness.FallbackId);
    }

    [Fact]
    public async Task Failover_AlsoCoversTheTypedAnswer()
    {
        using var h = new Harness();
        h.Primary.EnqueueError(new HttpRequestException("primary is down"));
        h.Fallback.EnqueueOutputs(Harness.Reply("From the fallback."));

        var answer = await h.Backend.AskAsync("Hello?", Ct);

        answer.Answer.ShouldBe("From the fallback.");
        h.Fallback.RecordedCalls.Single().Options!.ModelId.ShouldBe(Harness.FallbackId);
    }

    [Fact]
    public async Task Failover_WhenBothModelsFail_TheErrorSurfaces()
    {
        using var h = new Harness();
        h.Primary.EnqueueError(new HttpRequestException("primary is down"));
        h.Fallback.EnqueueError(new HttpRequestException("fallback is down"));

        var error = await Should.ThrowAsync<HttpRequestException>(() => h.Backend.QuickReplyAsync("Hello?", Ct));

        error.Message.ShouldBe("fallback is down");
    }
}
