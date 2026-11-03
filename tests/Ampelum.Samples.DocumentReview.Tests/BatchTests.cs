using Ampelum.Samples.DocumentReview;
using Shouldly;
using SignalGrid.Ampelum.Batch;
using Xunit;

namespace Ampelum.Samples.DocumentReview.Tests;

public sealed class BatchTests
{
    static async Task<List<LoadedFile>> OvernightAsync(Rig rig) =>
        [.. (await rig.ReadableAsync(TestContext.Current.CancellationToken)).Where(f => SupplierDocuments.Overnight.Contains(f.Name))];

    [Fact(Timeout = 30000)]
    public async Task Submit_UploadsOneLinePerDocumentWithTheConfiguredModel()
    {
        await using var rig = Rig.Create();
        var files = await OvernightAsync(rig);
        using var queue = rig.Desk.OpenQueue();

        var handle = await queue.SubmitAsync(files, TestContext.Current.CancellationToken);

        handle.Id.ShouldBe("batch-1");
        handle.Provider.ShouldBe(OpenAIBatchSubmitter.ProviderName);
        handle.SubmittedAt.ShouldBe(rig.Clock.GetUtcNow());
        rig.Batch.Calls.ShouldBe(["POST /v1/files", "POST /v1/batches"]);
        rig.Batch.Submitted.Select(l => l.CustomId).ShouldBe(files.Select(f => f.Name));
        rig.Batch.Submitted.ShouldAllBe(l => l.Model == FakeData.BatchModel);
        rig.Batch.Submitted.Select(l => l.UserText).ShouldBe(files.Select(f => f.Text));
    }

    [Fact(Timeout = 30000)]
    public async Task Wait_PollsThroughTheGivenClockUntilTheBatchCompletes()
    {
        await using var rig = Rig.Create(inProgressPolls: 2);
        var files = await OvernightAsync(rig);
        using var queue = rig.Desk.OpenQueue();
        var handle = await queue.SubmitAsync(files, TestContext.Current.CancellationToken);

        var status = await queue.WaitAsync(handle, TestContext.Current.CancellationToken);

        status.State.ShouldBe(BatchState.Completed);
        status.CompletedCount.ShouldBe(files.Count);
        status.TotalCount.ShouldBe(files.Count);
        rig.Clock.Delays.ShouldBe([ReviewDesk.PollInterval, ReviewDesk.PollInterval]);
        rig.Batch.Calls.Count(c => c == "GET /v1/batches/batch-1").ShouldBe(3);
    }

    [Fact(Timeout = 30000)]
    public async Task Results_ComeBackPerDocumentFromTheOutputFile()
    {
        await using var rig = Rig.Create();
        var files = await OvernightAsync(rig);
        using var queue = rig.Desk.OpenQueue();
        var handle = await queue.SubmitAsync(files, TestContext.Current.CancellationToken);
        await queue.WaitAsync(handle, TestContext.Current.CancellationToken);

        var results = await queue.ResultsAsync(handle, TestContext.Current.CancellationToken);

        results.Select(r => r.CustomId).ShouldBe(files.Select(f => f.Name));
        foreach (var (item, file) in results.Zip(files))
        {
            item.ErrorMessage.ShouldBeNull();
            item.Response!.Text.ShouldStartWith($"Summary of {file.Name}: {file.Text[..20]}");
        }
        rig.Batch.Calls.ShouldContain("GET /v1/files/out-batch-1/content");
    }

    [Fact(Timeout = 30000)]
    public async Task Cancel_StopsTheBatchAndStatusSaysSo()
    {
        await using var rig = Rig.Create(inProgressPolls: 5);
        var withdrawn = (await rig.ReadableAsync(TestContext.Current.CancellationToken)).Where(f => f.Name == SupplierDocuments.Withdrawn).ToList();
        using var queue = rig.Desk.OpenQueue();
        var handle = await queue.SubmitAsync(withdrawn, TestContext.Current.CancellationToken);

        var status = await queue.CancelAsync(handle, TestContext.Current.CancellationToken);

        status.State.ShouldBe(BatchState.Cancelled);
        rig.Batch.Calls.ShouldContain("POST /v1/batches/batch-1/cancel");
    }

    [Fact(Timeout = 30000)]
    public async Task Submit_SendsTheConfiguredKey()
    {
        var wrongKey = Rig.SettingsOf((ReviewSettings.BatchKeyName, "not-the-key"), (ReviewSettings.BatchModelName, FakeData.BatchModel));
        await using var rig = Rig.Create(settings: wrongKey);
        var files = await OvernightAsync(rig);
        using var queue = rig.Desk.OpenQueue();

        await Should.ThrowAsync<HttpRequestException>(() => queue.SubmitAsync(files, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30000)]
    public async Task Wait_GivesUpAfterThePollingLimit()
    {
        await using var rig = Rig.Create(inProgressPolls: int.MaxValue);
        var files = await OvernightAsync(rig);
        using var queue = rig.Desk.OpenQueue();
        var handle = await queue.SubmitAsync(files, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<TimeoutException>(() => queue.WaitAsync(handle, TestContext.Current.CancellationToken));

        rig.Clock.Delays.Count.ShouldBe(ReviewDesk.MaxPolls);
    }

    [Fact]
    public async Task OpenQueue_MissingBatchSettings_NamesTheSetting()
    {
        await using var rig = Rig.Create(settings: Rig.SettingsOf((ReviewSettings.BatchKeyName, "k")));

        Should.Throw<InvalidOperationException>(() => rig.Desk.OpenQueue()).Message.ShouldContain(ReviewSettings.BatchModelName);
    }
}
