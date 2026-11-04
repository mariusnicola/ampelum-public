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

    static string WritePromptFile(TempStorageFixture fix, string relativePath, DateTime writtenUtc)
    {
        var path = Path.Combine(fix.PromptDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"demoAssignments":{},"instructionOverrides":{"default":"Be brief."}}""");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public async Task Rebuild_PromptFilesInAModuleFolder_AreIndexedUnderThatModule()
    {
        using var fix = new TempStorageFixture();
        var path = WritePromptFile(fix, Path.Combine("modA", "v-one.json"), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var sut = MakeSut(fix);

        var summary = await sut.RebuildAsync(TestContext.Current.CancellationToken);

        summary.PromptVersionsIndexed.ShouldBe(1);
        var store = new PromptVersionStore(TestData.Factory(fix.Options));
        var rows = await store.ListByModuleAsync("modA", TestContext.Current.CancellationToken);
        var row = rows.ShouldHaveSingleItem();
        row.VersionId.ShouldBe("v-one");
        row.ModuleName.ShouldBe("modA");
        row.FilePath.ShouldBe(path);
        row.Status.ShouldBe("challenger");
        (await store.ListModulesAsync(TestContext.Current.CancellationToken)).ShouldBe(["modA"]);
    }

    [Fact]
    public async Task Rebuild_PromptFilesOfTwoModules_AreKeptApart()
    {
        using var fix = new TempStorageFixture();
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        WritePromptFile(fix, Path.Combine("modA", "v-a.json"), when);
        WritePromptFile(fix, Path.Combine("modB", "v-b.json"), when);
        var sut = MakeSut(fix);

        var summary = await sut.RebuildAsync(TestContext.Current.CancellationToken);

        summary.PromptVersionsIndexed.ShouldBe(2);
        var store = new PromptVersionStore(TestData.Factory(fix.Options));
        (await store.ListByModuleAsync("modA", TestContext.Current.CancellationToken)).Select(r => r.VersionId).ShouldBe(["v-a"]);
        (await store.ListByModuleAsync("modB", TestContext.Current.CancellationToken)).Select(r => r.VersionId).ShouldBe(["v-b"]);
    }

    [Fact]
    public async Task Rebuild_APromptFileDirectlyInThePromptDir_IsStillIndexed_UnderTheModuleUnknown()
    {
        using var fix = new TempStorageFixture();
        WritePromptFile(fix, "v-top.json", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var sut = MakeSut(fix);

        var summary = await sut.RebuildAsync(TestContext.Current.CancellationToken);

        summary.PromptVersionsIndexed.ShouldBe(1);
        var store = new PromptVersionStore(TestData.Factory(fix.Options));
        (await store.ListByModuleAsync("unknown", TestContext.Current.CancellationToken)).ShouldHaveSingleItem().VersionId.ShouldBe("v-top");
    }

    [Fact]
    public async Task Rebuild_PromptVersions_CarryTheirFilesWriteTime_SoTheyKeepTheirOrder()
    {
        using var fix = new TempStorageFixture();
        var older = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var newer = older.AddHours(5);
        // The newer file sorts first by name, so the order cannot come from the names.
        WritePromptFile(fix, Path.Combine("modA", "v-a-newer.json"), newer);
        WritePromptFile(fix, Path.Combine("modA", "v-b-older.json"), older);
        var sut = MakeSut(fix);

        await sut.RebuildAsync(TestContext.Current.CancellationToken);

        var rows = await new PromptVersionStore(TestData.Factory(fix.Options)).ListByModuleAsync("modA", TestContext.Current.CancellationToken);
        rows.Select(r => r.VersionId).ShouldBe(["v-b-older", "v-a-newer"]);
        rows[0].CreatedAt.UtcDateTime.ShouldBe(older);
        rows[1].CreatedAt.UtcDateTime.ShouldBe(newer);
    }

    [Fact]
    public async Task Rebuild_ReplacesTheRowsAlreadyThere_WithTheFilesOnDisk()
    {
        using var fix = new TempStorageFixture();
        var store = new PromptVersionStore(TestData.Factory(fix.Options));
        await store.InsertChallengerAsync(
            new PromptVersionRow("v-gone", "modA", "challenger", DateTimeOffset.UtcNow, null, "/no/such/file.json", null),
            TestContext.Current.CancellationToken);
        WritePromptFile(fix, Path.Combine("modA", "v-here.json"), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var sut = MakeSut(fix);

        await sut.RebuildAsync(TestContext.Current.CancellationToken);

        (await store.ListByModuleAsync("modA", TestContext.Current.CancellationToken)).Select(r => r.VersionId).ShouldBe(["v-here"]);
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
