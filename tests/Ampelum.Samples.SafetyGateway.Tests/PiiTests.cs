using Ampelum.Samples.SafetyGateway;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.SafetyGateway.Tests;

public sealed class PiiTests
{
    const string Email = "jo.keller@example.test";
    const string Phone = "555-123-4567";

    [Fact]
    public async Task Question_WithContactDetails_ReachesTheModelAsTyped_AndIsRedactedOnlyInTheAuditFile()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, FakeScript.LeaveAnswer, 100, 10);

        var reply = await h.Gateway.AskAsync(DemoQuestions.Leave, TestContext.Current.CancellationToken);

        // What really happens: the prompt and the trace keep the original text.
        h.Sent(0).ShouldContain(Email);
        h.Sent(0).ShouldContain(Phone);
        h.Trace.Records.Single().PromptMessages.ShouldContain(m => m.Text.Contains(Email));
        reply.Text.ShouldBe(FakeScript.LeaveAnswer);

        // The audit file is where the redaction lands.
        var values = await h.AuditValuesAsync();
        values.ShouldContain("<REDACTED:EMAIL>");
        values.ShouldContain("<REDACTED:PHONE>");
        h.AuditText().ShouldNotContain(Email);
        h.AuditText().ShouldNotContain(Phone);
    }

    [Fact]
    public async Task ModelAnswer_WithAnEmail_IsRedactedInTheAuditFileButReturnedAsTheModelWroteIt()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, $"Ask {Email} about it.", 100, 10);

        var reply = await h.Gateway.AskAsync("Who handles benefits?", TestContext.Current.CancellationToken);

        reply.Text.ShouldContain(Email);
        h.AuditText().ShouldNotContain(Email);
        (await h.AuditValuesAsync()).ShouldContain("Ask <REDACTED:EMAIL> about it.");
    }

    [Fact]
    public async Task SsnAndLuhnValidCard_AreRedactedInTheAuditFile()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "ok", 10, 1);

        await h.Gateway.AskAsync("My SSN is 123-45-6789 and my card is 4111 1111 1111 1111.", TestContext.Current.CancellationToken);

        h.AuditText().ShouldNotContain("123-45-6789");
        h.AuditText().ShouldNotContain("4111");
        (await h.AuditValuesAsync()).ShouldContain("<REDACTED:SSN>");
    }

    [Fact]
    public async Task DigitRun_ThatFailsTheLuhnCheck_IsKeptOutOfTheCardRule()
    {
        await using var h = await Harness.StartAsync();
        FakeScript.EnqueueAnswer(h.Chat, "ok", 10, 1);

        await h.Gateway.AskAsync("Order reference 1234 5678 9012 3456 is late.", TestContext.Current.CancellationToken);

        (await h.AuditValuesAsync()).ShouldNotContain("<REDACTED:CC>");
    }
}
