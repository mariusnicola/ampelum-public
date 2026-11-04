using Ampelum.Samples.DocumentReview;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class RunTests
{
    static IReadOnlyList<(RerankerKind Kind, string Key, string Model)> BothRerankers() =>
        [(RerankerKind.Cohere, FakeData.RerankerKey, FakeData.RerankerModel), (RerankerKind.Voyage, FakeData.RerankerKey, FakeData.RerankerModel)];

    [Fact(Timeout = 30000)]
    public async Task Run_WithAllServicesFaked_Succeeds()
    {
        await using var rig = Rig.Create();
        var output = new StringWriter();

        var exit = await ReviewRun.RunAsync(rig.Settings, rig.Transport, BothRerankers, rig.WorkDir, output, TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        var text = output.ToString();
        text.ShouldContain("damaged-scan.pdf: 'damaged-scan.pdf' is not a readable PDF");
        text.ShouldContain("Accepted: supply-agreement.pdf#page=1");
        text.ShouldContain("Verdict:  subcontract-notice.pdf#page=1: completed");
        text.ShouldContain("Status:   Completed, 3 of 3 done");
        text.ShouldContain("Cancel:   batch-2 is now Cancelled");
        rig.Rerank.Calls.Select(c => c.Kind).ShouldBe([RerankerKind.Cohere, RerankerKind.Voyage]);
        rig.Agent.Calls.ShouldHaveSingleItem();
        rig.Batch.Calls.ShouldContain("POST /v1/batches/batch-2/cancel");
    }

    [Fact(Timeout = 30000)]
    public async Task Run_RemoteReviewerError_ExitsNonZero()
    {
        await using var rig = Rig.Create();
        rig.Agent.RpcError = "reviewer unavailable";

        var exit = await ReviewRun.RunAsync(rig.Settings, rig.Transport, BothRerankers, rig.WorkDir, new StringWriter(), TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
    }

    [Fact(Timeout = 30000)]
    public async Task RunReportingErrors_PollingLimit_IsAnErrorLineAndExitOne()
    {
        await using var rig = Rig.Create(inProgressPolls: int.MaxValue);
        var errors = new StringWriter();

        var exit = await ReviewRun.RunReportingErrorsAsync(
            rig.Settings, rig.Transport, BothRerankers, rig.WorkDir, new StringWriter(), errors, TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        errors.ToString().ShouldStartWith("[ERROR] ");
        errors.ToString().ShouldContain("polling limit");
    }

    [Fact(Timeout = 30000)]
    public async Task RunReportingErrors_MissingSetting_NamesItInTheErrorLine()
    {
        await using var rig = Rig.Create(settings: Rig.SettingsOf());
        var errors = new StringWriter();

        var exit = await ReviewRun.RunReportingErrorsAsync(
            rig.Settings, rig.Transport, () => [rig.Settings.Reranker()], rig.WorkDir, new StringWriter(), errors, TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        errors.ToString().ShouldContain(ReviewSettings.RerankerName);
    }

    [Fact(Timeout = 30000)]
    public async Task Run_MissingBatchSetting_FailsAtTheBatchPartAfterTheEarlierPartsRan()
    {
        var settings = Rig.SettingsOf(
            (ReviewSettings.RerankerName, "voyage"),
            (ReviewSettings.RerankerKeyName, FakeData.RerankerKey),
            (ReviewSettings.RerankerModelName, FakeData.RerankerModel),
            (ReviewSettings.AgentUrlName, FakeData.AgentBase.ToString()));
        await using var rig = Rig.Create(settings: settings);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ReviewRun.RunAsync(rig.Settings, rig.Transport, () => [settings.Reranker()], rig.WorkDir, new StringWriter(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(ReviewSettings.BatchKeyName);
        rig.Rerank.Calls.ShouldHaveSingleItem().Kind.ShouldBe(RerankerKind.Voyage);
        rig.Agent.Calls.ShouldHaveSingleItem();
        rig.Batch.Calls.ShouldBeEmpty();
    }
}
