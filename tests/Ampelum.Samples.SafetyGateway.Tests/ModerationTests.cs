using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.Audit;
using SignalGrid.Ampelum.Safety.Moderation;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class ModerationTests
{
    [Fact]
    public async Task CleanReply_IsReleased_AndModerationWasCalledWithTheKeyFromTheSecretStore()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, FakeScript.LeaveAnswer, 100, 10);

        var reply = await h.Gateway.AskAsync("How many leave days?", TestContext.Current.CancellationToken);

        reply.Text.ShouldBe(FakeScript.LeaveAnswer);
        var request = h.Moderation.Requests.Single();
        request.Authorization.ShouldBe($"Bearer {FakeData.ModerationKey}");
        request.Input.ShouldContain(FakeScript.LeaveAnswer);
        h.Gateway.AuditEntries.ShouldBe(1);
    }

    [Fact]
    public async Task FlaggedReply_IsHeldBack_AndTheAuditTrailRecordsTheBlockNotTheText()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, $"Here you go. {FakeData.UnsafeMarker}", 100, 10);

        var error = await Should.ThrowAsync<ModerationException>(
            () => h.Gateway.AskAsync(DemoQuestions.Unsafe, TestContext.Current.CancellationToken));

        error.Categories.ShouldBe([FakeData.FakeModerationCategory]);
        h.Moderation.Requests.Single().Input.ShouldContain(FakeData.UnsafeMarker);
        var entry = (await h.AuditEntriesAsync()).Single();
        entry.EventType.ShouldBe(SafetyNotes.ReplyBlocked);
        entry.Outputs["categories"].ShouldBe(FakeData.FakeModerationCategory);
        h.AuditText().ShouldNotContain(FakeData.UnsafeMarker);
        (await h.Gateway.VerifyAuditAsync(TestContext.Current.CancellationToken)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task ModerationServiceDown_FailsClosed_AndTheOutageIsNotedInsteadOfTheReply()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, FakeScript.LeaveAnswer, 100, 10);
        h.Moderation.FailWith = new HttpRequestException("moderation unreachable");

        await Should.ThrowAsync<ModerationUnavailableException>(
            () => h.Gateway.AskAsync("How many leave days?", TestContext.Current.CancellationToken));

        var entry = (await h.AuditEntriesAsync()).Single();
        entry.EventType.ShouldBe(SafetyNotes.ModerationUnavailable);
        entry.Outputs["error"].ShouldBe(nameof(HttpRequestException));
        h.AuditText().ShouldNotContain(FakeScript.LeaveAnswer);
    }

    [Fact]
    public async Task ModerationTimeout_IsAnOutage_NotedAndFailsClosed()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, FakeScript.LeaveAnswer, 100, 10);
        // HttpClient reports its own timeout as a cancellation the caller did not ask for.
        h.Moderation.FailWith = new TaskCanceledException("The request was canceled due to the configured timeout.");

        var error = await Should.ThrowAsync<ModerationUnavailableException>(
            () => h.Gateway.AskAsync("How many leave days?", TestContext.Current.CancellationToken));

        error.InnerException.ShouldBeOfType<TaskCanceledException>();
        var entry = (await h.AuditEntriesAsync()).Single();
        entry.EventType.ShouldBe(SafetyNotes.ModerationUnavailable);
        entry.Outputs["error"].ShouldBe(nameof(TaskCanceledException));
        h.AuditText().ShouldNotContain(FakeScript.LeaveAnswer);
    }

    [Fact]
    public async Task MalformedModerationBody_IsAnOutage_NotedAndFailsClosed()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, FakeScript.LeaveAnswer, 100, 10);
        h.Moderation.RawBody = "this is not json";

        await Should.ThrowAsync<ModerationUnavailableException>(
            () => h.Gateway.AskAsync("How many leave days?", TestContext.Current.CancellationToken));

        (await h.AuditEntriesAsync()).Single().EventType.ShouldBe(SafetyNotes.ModerationUnavailable);
        h.AuditText().ShouldNotContain(FakeScript.LeaveAnswer);
    }

    sealed class CancelledModerator : SignalGrid.Ampelum.Safety.Moderation.IOutputModerator
    {
        public ValueTask<SignalGrid.Ampelum.Safety.Moderation.ModerationResult> ModerateAsync(string text, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("stopped");
    }

    [Fact]
    public async Task CancellationRequestedByTheCaller_StaysACancellation_NotAnOutage()
    {
        // The wrapper is called directly: through the pipeline a cancelled token is also thrown by other code, which hides the difference.
        var moderator = new AuditedModerator(new CancelledModerator());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await moderator.ModerateAsync("text", cts.Token));

        error.ShouldNotBeOfType<ModerationUnavailableException>();
        // Without a cancel request the same exception is an outage.
        await Should.ThrowAsync<ModerationUnavailableException>(async () => await moderator.ModerateAsync("text", CancellationToken.None));
    }
}
