#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class OptimizerRunStoreTests
{
    private static OptimizerRunStore MakeSut(TempStorageFixture fix) =>
        new(TestData.Factory(fix.Options));

    [Fact]
    public async Task Insert_CreatesInQueuedState()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, null, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        var found = await sut.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.State.ShouldBe("queued");
    }

    [Fact]
    public async Task UpdateProgress_FromQueued_TransitionsToRunning()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, 10, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        await sut.UpdateProgressAsync("r1", 5, 10, TestContext.Current.CancellationToken);
        var found = await sut.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found!.State.ShouldBe("running");
        found.TrialsCompleted.ShouldBe(5);
    }

    [Fact]
    public async Task UpdateProgress_FromRunning_StaysRunning()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, 10, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        await sut.UpdateProgressAsync("r1", 5, 10, TestContext.Current.CancellationToken);
        await sut.UpdateProgressAsync("r1", 8, 10, TestContext.Current.CancellationToken);
        var found = await sut.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found!.State.ShouldBe("running");
        found.TrialsCompleted.ShouldBe(8);
    }

    [Fact]
    public async Task UpdateProgress_NotQueued_Throws()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.UpdateProgressAsync("nope", 0, 10, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task Complete_FromRunning_Succeeds()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, 10, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        await sut.UpdateProgressAsync("r1", 10, 10, TestContext.Current.CancellationToken);
        await sut.CompleteAsync("r1", "succeeded", "v2", null, TestContext.Current.CancellationToken);
        var found = await sut.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found!.State.ShouldBe("succeeded");
        found.ResultVersionId.ShouldBe("v2");
        found.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Complete_NotRunning_Throws()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, 10, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.CompleteAsync("r1", "failed", null, "error", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not 'running'");
    }

    [Fact]
    public async Task TryGet_Missing_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var found = await sut.TryGetAsync("nope", TestContext.Current.CancellationToken);
        found.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAll_ClearsAllRuns()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, null, null, null);
        await sut.InsertAsync(row, TestContext.Current.CancellationToken);
        await sut.DeleteAllAsync(TestContext.Current.CancellationToken);
        var found = await sut.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found.ShouldBeNull();
    }
}
