#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class PromptVersionStoreTests
{
    private static PromptVersionStore MakeSut(TempStorageFixture fix) =>
        new(TestData.Factory(fix.Options));

    [Fact]
    public async Task ReplaceAll_ReplacesEveryRowWithTheGivenOnes_AndKeepsTheirStatus()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.InsertChallengerAsync(new PromptVersionRow("v-gone", "modA", "challenger", ts, null, "/p0", null), TestContext.Current.CancellationToken);

        await sut.ReplaceAllAsync(
            [new PromptVersionRow("v1", "modA", "active", ts, null, "/p1", "n"), new PromptVersionRow("v2", "modB", "retired", ts, "v1", "/p2", null)],
            TestContext.Current.CancellationToken);

        (await sut.TryGetByIdAsync("v-gone", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await sut.GetActiveAsync("modA", TestContext.Current.CancellationToken))!.VersionId.ShouldBe("v1");
        (await sut.TryGetByIdAsync("v2", TestContext.Current.CancellationToken))!.Status.ShouldBe("retired");
    }

    [Fact]
    public async Task ReplaceAll_WhenOneRowFails_LeavesTheRowsThatWereThere()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.InsertChallengerAsync(new PromptVersionRow("v-before", "modA", "challenger", ts, null, "/p0", null), TestContext.Current.CancellationToken);

        // The second row repeats the first one's version id.
        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(async () => await sut.ReplaceAllAsync(
            [new PromptVersionRow("dup", "modA", "challenger", ts, null, "/p1", null), new PromptVersionRow("dup", "modB", "challenger", ts, null, "/p2", null)],
            TestContext.Current.CancellationToken));

        (await sut.TryGetByIdAsync("v-before", TestContext.Current.CancellationToken))!.VersionId.ShouldBe("v-before");
        (await sut.TryGetByIdAsync("dup", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task ListModules_Empty_ReturnsEmptyList()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var modules = await sut.ListModulesAsync(TestContext.Current.CancellationToken);
        modules.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListModules_WithVersions_ReturnsUnique()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var row1 = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null);
        var row2 = new PromptVersionRow("v2", "modA", "challenger", ts, null, "/p2", null);
        var row3 = new PromptVersionRow("v3", "modB", "challenger", ts, null, "/p3", null);
        await sut.InsertChallengerAsync(row1, TestContext.Current.CancellationToken);
        await sut.InsertChallengerAsync(row2, TestContext.Current.CancellationToken);
        await sut.InsertChallengerAsync(row3, TestContext.Current.CancellationToken);
        var modules = await sut.ListModulesAsync(TestContext.Current.CancellationToken);
        modules.ShouldBe(new[] { "modA", "modB" });
    }

    [Fact]
    public async Task GetActive_NoActive_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var active = await sut.GetActiveAsync("modA", TestContext.Current.CancellationToken);
        active.ShouldBeNull();
    }

    [Fact]
    public async Task GetActive_OneActive_ReturnsIt()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var row = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null);
        await sut.InsertChallengerAsync(row, TestContext.Current.CancellationToken);
        await sut.PromoteAtomicallyAsync("modA", "v1", TestContext.Current.CancellationToken);
        var active = await sut.GetActiveAsync("modA", TestContext.Current.CancellationToken);
        active.ShouldNotBeNull();
        active!.VersionId.ShouldBe("v1");
        active.Status.ShouldBe("active");
    }

    [Fact]
    public async Task PromoteAtomically_NoPriorActive_TransitionsToActive()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var row = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null);
        await sut.InsertChallengerAsync(row, TestContext.Current.CancellationToken);
        var result = await sut.PromoteAtomicallyAsync("modA", "v1", TestContext.Current.CancellationToken);
        result.NewActiveVersionId.ShouldBe("v1");
        result.PreviousActiveVersionId.ShouldBeNull();
    }

    [Fact]
    public async Task PromoteAtomically_WithPriorActive_RetiresIt()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var v1 = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null);
        var v2 = new PromptVersionRow("v2", "modA", "challenger", ts.AddMinutes(1), "v1", "/p2", null);
        await sut.InsertChallengerAsync(v1, TestContext.Current.CancellationToken);
        await sut.PromoteAtomicallyAsync("modA", "v1", TestContext.Current.CancellationToken);
        await sut.InsertChallengerAsync(v2, TestContext.Current.CancellationToken);
        var result = await sut.PromoteAtomicallyAsync("modA", "v2", TestContext.Current.CancellationToken);
        result.NewActiveVersionId.ShouldBe("v2");
        result.PreviousActiveVersionId.ShouldBe("v1");

        var v1After = await sut.TryGetByIdAsync("v1", TestContext.Current.CancellationToken);
        v1After!.Status.ShouldBe("retired");
    }

    [Fact]
    public async Task PromoteAtomically_NonChallenger_ThrowsConflict()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var row = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null);
        await sut.InsertChallengerAsync(row, TestContext.Current.CancellationToken);
        await sut.PromoteAtomicallyAsync("modA", "v1", TestContext.Current.CancellationToken);
        var ex = await Should.ThrowAsync<PromoteConflictException>(
            async () => await sut.PromoteAtomicallyAsync("modA", "v1", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not a challenger");
    }

    [Fact]
    public async Task PromoteAtomically_UnknownVersion_ThrowsConflict()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ex = await Should.ThrowAsync<PromoteConflictException>(
            async () => await sut.PromoteAtomicallyAsync("modA", "nope", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task TryGetById_Missing_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = await sut.TryGetByIdAsync("nope", TestContext.Current.CancellationToken);
        row.ShouldBeNull();
    }

    [Fact]
    public async Task TryGetById_Exists_ReturnsRow()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        var input = new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", "test note");
        await sut.InsertChallengerAsync(input, TestContext.Current.CancellationToken);
        var found = await sut.TryGetByIdAsync("v1", TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.VersionId.ShouldBe("v1");
        found.Note.ShouldBe("test note");
    }

    [Fact]
    public async Task ListByModule_Empty_ReturnsEmptyList()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var rows = await sut.ListByModuleAsync("modA", TestContext.Current.CancellationToken);
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListByModule_Multiple_ReturnsAllForModule()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.InsertChallengerAsync(new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null), TestContext.Current.CancellationToken);
        await sut.InsertChallengerAsync(new PromptVersionRow("v2", "modA", "challenger", ts.AddMinutes(1), null, "/p2", null), TestContext.Current.CancellationToken);
        await sut.InsertChallengerAsync(new PromptVersionRow("v3", "modB", "challenger", ts, null, "/p3", null), TestContext.Current.CancellationToken);
        var rows = await sut.ListByModuleAsync("modA", TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeleteAll_ClearsAllRows()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.InsertChallengerAsync(new PromptVersionRow("v1", "modA", "challenger", ts, null, "/p1", null), TestContext.Current.CancellationToken);
        await sut.DeleteAllAsync(TestContext.Current.CancellationToken);
        var modules = await sut.ListModulesAsync(TestContext.Current.CancellationToken);
        modules.ShouldBeEmpty();
    }
}
