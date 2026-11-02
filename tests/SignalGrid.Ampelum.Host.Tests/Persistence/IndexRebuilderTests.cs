#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class IndexRebuilderTests
{
    private IndexRebuilder MakeSut(TempStorageFixture fix, IModuleRegistry? registry = null)
    {
        registry ??= new global::SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry();
        return new IndexRebuilder(
            TestData.Factory(fix.Options),
            new AuditIndex(TestData.Factory(fix.Options)),
            new PromptVersionStore(TestData.Factory(fix.Options)),
            new OptimizerRunStore(TestData.Factory(fix.Options)),
            new ModuleRegistrySnapshotStore(TestData.Factory(fix.Options)),
            registry,
            Microsoft.Extensions.Options.Options.Create(fix.Options));
    }

    [Fact]
    public async Task Rebuild_EmptyAudit_ReturnsZeroCount()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var summary = await sut.RebuildAsync(TestContext.Current.CancellationToken);
        summary.AuditEntriesIndexed.ShouldBe(0);
        summary.PromptVersionsIndexed.ShouldBe(0);
    }

    [Fact]
    public async Task Rebuild_WithMissingAuditFile_SkipsAudit()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var summary = await sut.RebuildAsync(TestContext.Current.CancellationToken);
        summary.AuditEntriesIndexed.ShouldBe(0);
    }

    [Fact]
    public async Task Rebuild_OptimizerRuns_AlwaysClears()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var store = new OptimizerRunStore(TestData.Factory(fix.Options));
        var row = new OptimizerRunRow("r1", "modA", "MIPRO", "queued", DateTimeOffset.UtcNow, null, 0, 10, null, null);
        await store.InsertAsync(row, TestContext.Current.CancellationToken);
        await sut.RebuildAsync(TestContext.Current.CancellationToken);
        var found = await store.TryGetAsync("r1", TestContext.Current.CancellationToken);
        found.ShouldBeNull();
    }

    [Fact]
    public async Task Rebuild_ConcurrentCalls_Serialized()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var task1 = sut.RebuildAsync(TestContext.Current.CancellationToken);
        var task2 = sut.RebuildAsync(TestContext.Current.CancellationToken);
        var result1 = await task1;
        var result2 = await task2;
        result1.ShouldNotBeNull();
        result2.ShouldNotBeNull();
    }
}
