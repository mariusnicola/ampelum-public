using Ampelum.Samples.DocumentReview;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class AgentTests
{
    static readonly RankedClause Hard = new("subcontract-notice.pdf#page=1", "Clause 3. Liability follows applicable law.", 0.3);

    [Fact]
    public async Task Escalate_ReadsTheCardThenSendsTheClauseAndReturnsTheResult()
    {
        await using var rig = Rig.Create();

        var (agent, verdicts) = await rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken);

        agent.ShouldBe("supplier-clause-reviewer");
        rig.Agent.CardRequests.ShouldHaveSingleItem();
        var sent = rig.Agent.Calls.ShouldHaveSingleItem();
        sent.Text.ShouldContain(Hard.Id);
        sent.Text.ShouldContain(Hard.Text);
        sent.Text.ShouldContain(SupplierDocuments.Question);
        var verdict = verdicts.ShouldHaveSingleItem();
        verdict.ClauseId.ShouldBe(Hard.Id);
        verdict.Completed.ShouldBeTrue();
        verdict.Text.ShouldContain("Needs legal sign-off.");
        verdict.Text.ShouldContain(Hard.Text);
    }

    [Fact]
    public async Task Escalate_SendsOneTaskPerClause()
    {
        await using var rig = Rig.Create();
        var second = new RankedClause("other.pdf#page=1", "Clause 8. Notice period is ten days.", 0.1);

        var (_, verdicts) = await rig.Desk.EscalateAsync([Hard, second], SupplierDocuments.Question, TestContext.Current.CancellationToken);

        rig.Agent.Calls.Count.ShouldBe(2);
        verdicts.Select(v => v.ClauseId).ShouldBe([Hard.Id, second.Id]);
        verdicts[1].Text.ShouldContain(second.Text);
    }

    [Fact]
    public async Task Escalate_CardNamingAnotherHost_SendsNothing()
    {
        await using var rig = Rig.Create();
        rig.Agent.CardUrl = new Uri("http://127.0.0.1:9999/rpc");

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("Nothing was sent");
        rig.Agent.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Escalate_CardWithACapabilityOutsideTheKnownFlags_SendsNothing()
    {
        await using var rig = Rig.Create();
        rig.Agent.CardCapabilities =
        [
            new SignalGrid.Ampelum.Agent.A2A.A2AAgentCapability { Streaming = true, Custom = new Dictionary<string, object?> { ["exec"] = "rm -rf /" } },
        ];

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("'exec' is not a known capability");
        ex.Message.ShouldContain("Nothing was sent");
        rig.Agent.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Escalate_CardWithOnlyKnownFlags_GoesThrough()
    {
        await using var rig = Rig.Create();
        rig.Agent.CardCapabilities = [new SignalGrid.Ampelum.Agent.A2A.A2AAgentCapability { Streaming = true, LongRunningTasks = true }];

        var (_, verdicts) = await rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken);

        verdicts.ShouldHaveSingleItem().Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task Escalate_RemoteError_IsAFailedVerdictNotACompletedOne()
    {
        await using var rig = Rig.Create();
        rig.Agent.RpcError = "reviewer unavailable";

        var (_, verdicts) = await rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken);

        var verdict = verdicts.ShouldHaveSingleItem();
        verdict.Completed.ShouldBeFalse();
        verdict.Text.ShouldContain("reviewer unavailable");
    }

    [Fact]
    public async Task Escalate_MissingAgentUrl_NamesTheSetting()
    {
        await using var rig = Rig.Create(settings: Rig.SettingsOf());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            rig.Desk.EscalateAsync([Hard], SupplierDocuments.Question, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(ReviewSettings.AgentUrlName);
        rig.Agent.CardRequests.ShouldBeEmpty();
    }
}
