#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class ModuleRegistrySnapshotStoreTests
{
    private static ModuleRegistrySnapshotStore MakeSut(TempStorageFixture fix) =>
        new(TestData.Factory(fix.Options));

    [Fact]
    public async Task Upsert_ThenList_ReturnsRow()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new ModuleSnapshotRow("modA", "FakeSig", true, DateTimeOffset.UtcNow);
        await sut.UpsertAsync(row, TestContext.Current.CancellationToken);
        var list = await sut.ListAsync(TestContext.Current.CancellationToken);
        list.Count.ShouldBe(1);
        list[0].ModuleName.ShouldBe("modA");
        list[0].SupportsStream.ShouldBe(true);
    }

    [Fact]
    public async Task Upsert_Overwrite_UpdatesRow()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row1 = new ModuleSnapshotRow("modA", "FakeSig", true, DateTimeOffset.UtcNow);
        var row2 = new ModuleSnapshotRow("modA", "OtherSig", false, DateTimeOffset.UtcNow.AddMinutes(1));
        await sut.UpsertAsync(row1, TestContext.Current.CancellationToken);
        await sut.UpsertAsync(row2, TestContext.Current.CancellationToken);
        var list = await sut.ListAsync(TestContext.Current.CancellationToken);
        list.Count.ShouldBe(1);
        list[0].SignatureType.ShouldBe("OtherSig");
        list[0].SupportsStream.ShouldBe(false);
    }

    [Fact]
    public async Task List_Empty_ReturnsEmptyList()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var list = await sut.ListAsync(TestContext.Current.CancellationToken);
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAll_ClearsAllRows()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new ModuleSnapshotRow("modA", "FakeSig", true, DateTimeOffset.UtcNow);
        await sut.UpsertAsync(row, TestContext.Current.CancellationToken);
        await sut.DeleteAllAsync(TestContext.Current.CancellationToken);
        var list = await sut.ListAsync(TestContext.Current.CancellationToken);
        list.ShouldBeEmpty();
    }
}
