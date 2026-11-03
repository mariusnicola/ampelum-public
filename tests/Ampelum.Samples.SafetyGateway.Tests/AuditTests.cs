using Ampelum.Samples.SafetyGateway;
using Shouldly;
using SignalGrid.Ampelum.Audit;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class AuditTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachCall_IsOneJsonLine_AndTheChainVerifies()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "One.", 10, 1);
        FakeScript.EnqueueAnswer(h.Chat, "Two.", 10, 1);

        await h.Gateway.AskAsync("First?", Ct);
        await h.Gateway.AskAsync("Second?", Ct);

        h.Gateway.AuditSink.ShouldBeOfType<JsonFileAuditSink>();
        h.AuditText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
        var entries = await h.AuditEntriesAsync();
        entries.Select(e => e.Sequence).ShouldBe([0L, 1L]);
        entries.Select(e => e.Outputs["Answer"]).ShouldBe(["One.", "Two."]);
        entries[0].PrevHash.ShouldBe(new byte[entries[0].PrevHash.Length]);
        entries[1].PrevHash.ShouldBe(entries[0].EntryHash);
        (await h.Gateway.VerifyAuditAsync(Ct)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task EditedAnswerInTheFile_BreaksTheChainAtThatEntry()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "Employees get 25 days.", 10, 1);
        FakeScript.EnqueueAnswer(h.Chat, "Second.", 10, 1);
        await h.Gateway.AskAsync("First?", Ct);
        await h.Gateway.AskAsync("Second?", Ct);
        await h.Gateway.DisposeAsync();

        await File.WriteAllTextAsync(h.AuditPath, h.AuditText().Replace("Employees get 25 days.", "Employees get 99 days."), Ct);

        var verdict = await h.VerifyAuditFileAsync();
        verdict.IsValid.ShouldBeFalse();
        verdict.BreakAtSequence.ShouldBe(0L);
        verdict.Reason.ShouldContain("tampered");
    }

    [Fact]
    public async Task RemovedLine_BreaksTheChain()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "One.", 10, 1);
        FakeScript.EnqueueAnswer(h.Chat, "Two.", 10, 1);
        await h.Gateway.AskAsync("First?", Ct);
        await h.Gateway.AskAsync("Second?", Ct);
        await h.Gateway.DisposeAsync();

        var lines = h.AuditText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await File.WriteAllTextAsync(h.AuditPath, lines[1] + "\n", Ct);

        (await h.VerifyAuditFileAsync()).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Replay_AnswersFromTheFileInANewRun_WithoutCallingTheModel()
    {
        using var folder = new TempFolder();
        string original;
        await using (var first = await Harness.StartAsync(folder: folder))
        {
            FakeScript.EnqueueAnswer(first.Chat, FakeScript.LeaveAnswer, 100, 10);
            original = (await first.Gateway.AskAsync("How many leave days?", Ct)).Text;
        }

        await using var replay = await Harness.StartAsync(folder: folder, replay: true);
        var replayed = await replay.Gateway.AskAsync("How many leave days?", Ct);

        replayed.Text.ShouldBe(original);
        replay.Chat.RecordedCalls.ShouldBeEmpty();
        // The replayed reply still passes moderation, and nothing new is written to the file.
        replay.Moderation.Requests.Count.ShouldBe(1);
        (await replay.AuditEntriesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AuditEntry_CarriesTheTokenCountsOfTheModelCall()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "One.", 120, 18);

        await h.Gateway.AskAsync("First?", Ct);

        var entry = (await h.AuditEntriesAsync()).Single();
        entry.PromptTokens.ShouldBe(120L);
        entry.CompletionTokens.ShouldBe(18L);
    }

    [Fact]
    public async Task Replay_OfAQuestionThePiiHookChanged_AnswersFromTheFile()
    {
        using var folder = new TempFolder();
        await using (var first = await Harness.StartAsync(folder: folder))
        {
            FakeScript.EnqueueAnswer(first.Chat, FakeScript.LeaveAnswer, 100, 10);
            await first.Gateway.AskAsync(DemoQuestions.Leave, Ct);
        }

        await using var replay = await Harness.StartAsync(folder: folder, replay: true);
        var replayed = await replay.Gateway.AskAsync(DemoQuestions.Leave, Ct);

        replayed.Text.ShouldBe(FakeScript.LeaveAnswer);
        replay.Chat.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Replay_OfAQuestionThatWasNeverAsked_Fails()
    {
        using var folder = new TempFolder();
        await using (var first = await Harness.StartAsync(folder: folder))
        {
            FakeScript.EnqueueAnswer(first.Chat, FakeScript.LeaveAnswer, 100, 10);
            await first.Gateway.AskAsync("How many leave days?", Ct);
        }

        await using var replay = await Harness.StartAsync(folder: folder, replay: true);

        await Should.ThrowAsync<AuditReplayMissingException>(
            () => replay.Gateway.AskAsync("What is the dress code?", Ct));
        replay.Chat.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task BatchedAudit_HasEveryEntryOnDiskOnceTheGatewayIsDisposed()
    {
        await using var h = await Harness.StartAsync(o => o with { BatchAudit = true });
        for (var i = 0; i < 5; i++)
            FakeScript.EnqueueAnswer(h.Chat, $"Answer {i}.", 10, 1);

        for (var i = 0; i < 5; i++)
            await h.Gateway.AskAsync($"Question {i}?", Ct);
        h.Gateway.AuditSink.ShouldBeOfType<BatchedAuditSink>();
        h.Gateway.AuditEntries.ShouldBe(5);
        await h.Gateway.DisposeAsync();

        var entries = await h.AuditEntriesAsync();
        entries.Select(e => e.Outputs["Answer"]).ShouldBe(["Answer 0.", "Answer 1.", "Answer 2.", "Answer 3.", "Answer 4."]);
        (await h.VerifyAuditFileAsync()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task AuditFileThatAlreadyHasEntries_IsContinuedByANewRun()
    {
        await using var first = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(first.Chat, "One.", 10, 1);
        FakeScript.EnqueueAnswer(first.Chat, "Two.", 10, 1);
        await first.Gateway.AskAsync("First?", Ct);
        await first.Gateway.DisposeAsync();

        await using var second = await first.StartGatewayAsync();
        await second.AskAsync("Second?", Ct);
        await second.DisposeAsync();

        var entries = await first.AuditEntriesAsync();
        entries.Select(e => e.Sequence).ShouldBe([0L, 1L]);
        entries[1].PrevHash.ShouldBe(entries[0].EntryHash);
        (await first.VerifyAuditFileAsync()).IsValid.ShouldBeTrue();
    }
}
