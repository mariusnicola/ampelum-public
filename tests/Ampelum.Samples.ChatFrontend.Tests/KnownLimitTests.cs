using Shouldly;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Safety.Moderation;
using SignalGrid.Ampelum.Safety.Pii;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.ChatFrontend.Tests;

/// <summary>Each test pins something that does not work, so a reader sees a limit and a fix shows up as a failing test.</summary>
public sealed class KnownLimitTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KnownLimit_StreamingDoesNotFailOver_ThePrimaryErrorReachesTheCallerAndTheFallbackIsNeverAsked()
    {
        var primary = new DownChatClient();
        using var h = new Harness(primary: primary);
        h.Fallback.EnqueueStreamingChunks("Never", "sent");
        h.Fallback.EnqueueText("A one-shot call would have been rescued.");

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in h.Backend.OpenConversation().SendAsync("Hi", Ct)) { }
        });

        primary.Calls.ShouldBe(1);
        h.Fallback.RecordedCalls.ShouldBeEmpty();

        // The same outage on a one-shot call is rescued, so the gap is specific to streaming.
        (await h.Backend.QuickReplyAsync("Hi?", Ct)).Endpoint.ShouldBe(ChatBackend.FallbackEndpoint);
    }

    [Fact]
    public async Task KnownLimit_ConversationSendsTextBeforeTheAfterHooksAndGivesThemNoOutputs_SoModerationNeverSeesTheReply()
    {
        var delivered = 0;
        var spy = new AfterSpyHook(() => delivered);
        var moderator = new MarkerModerator();
        using var h = new Harness([new ModerationHook(moderator), spy]);
        h.Primary.EnqueueStreamingChunks("This reply is ", MarkerModerator.Marker, " text.");

        var tokens = new List<string>();
        await foreach (var token in h.Backend.OpenConversation().SendAsync("Hi", Ct))
        {
            tokens.Add(token);
            delivered++;
        }

        string.Concat(tokens).ShouldContain(MarkerModerator.Marker);
        spy.AfterRuns.ShouldBe(1);
        spy.DeliveredWhenAfterRan.ShouldBe(3);
        spy.OutputsWereNull.ShouldBe(true);
        moderator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task KnownLimit_ModerationHookDoesWork_OnTheTypedAnswer_WhichIsWhyTheConversationGapMatters()
    {
        var moderator = new MarkerModerator();
        using var h = new Harness([new ModerationHook(moderator)]);
        h.Primary.EnqueueOutputs(Harness.Reply($"This reply has {MarkerModerator.Marker} in it."));

        await Should.ThrowAsync<ModerationException>(() => h.Backend.AskAsync("Hi?", Ct));

        moderator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task SemanticCacheHit_StillRunsThePiiHook_SoTheAuditEntryHoldsTheRedactedInputs()
    {
        var audit = new InMemoryAuditSink();
        using var h = new Harness([new AuditHook(audit), new PiiRedactionHook(new RegexPiiRedactor())]);
        h.Primary.EnqueueOutputs(Harness.Reply("We will write to you."));
        const string question = "Please write to jane.doe@example.test about my order";

        await h.Backend.AskAsync(question, Ct);
        await h.Backend.AskAsync(question, Ct);

        h.Primary.RecordedCalls.Count.ShouldBe(1);
        var entries = new List<AuditEntry>();
        await foreach (var entry in audit.EnumerateAsync(Ct))
            entries.Add(entry);
        entries.Count.ShouldBe(2);
        // Both the miss and the hit ran the PII hook, so both audit entries hold the redacted text.
        entries[0].Inputs["Question"].ShouldBe("Please write to <REDACTED:EMAIL> about my order");
        entries[1].Inputs["Question"].ShouldBe("Please write to <REDACTED:EMAIL> about my order");
    }

    [Fact]
    public async Task KnownLimit_SemanticCacheMatchesIdenticalInputsOnly_ARewordedQuestionCallsTheModelAgain()
    {
        using var h = new Harness();
        h.Primary.EnqueueOutputs(Harness.Reply("Yes."));
        h.Primary.EnqueueOutputs(Harness.Reply("Yes, we do."));

        await h.Backend.AskAsync("Do you ship to Germany?", Ct);
        var reworded = await h.Backend.AskAsync("do you ship to germany", Ct);

        reworded.Answer.ShouldBe("Yes, we do.");
        h.Primary.RecordedCalls.Count.ShouldBe(2);
        h.Backend.CachedAnswers.ShouldBe(2);
    }

    [Fact]
    public async Task KnownLimit_SemanticCacheKeyIgnoresTheSignatureType_SoTwoSignaturesWithTheSameInputNamesShareAnEntry()
    {
        using var h = new Harness();
        h.Primary.EnqueueOutputs(Harness.Reply("A reply to the first signature."));
        var predictOther = h.Backend.Context;
        var other = new SignalGrid.Ampelum.Predict<OtherSignature>(predictOther);

        await h.Backend.AskAsync("Same words", Ct);
        var shared = await other.ForwardAsync(new OtherSignature { Question = "Same words" }, ct: Ct);

        // The second signature never reached the model; it was served the first one's cached outputs.
        h.Primary.RecordedCalls.Count.ShouldBe(1);
        shared.Value.Answer.ShouldBe("A reply to the first signature.");
        h.Backend.CachedAnswers.ShouldBe(1);
    }
}

[SignalGrid.Ampelum.Signature("Reply with a short answer.")]
public sealed record OtherSignature : SignalGrid.Ampelum.ISignature
{
    [SignalGrid.Ampelum.Input] public required string Question { get; init; }
    [SignalGrid.Ampelum.Output] public string Answer { get; init; } = "";
}
