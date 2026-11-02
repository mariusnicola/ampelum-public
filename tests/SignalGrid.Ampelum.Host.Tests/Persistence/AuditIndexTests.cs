#pragma warning disable AMPELUM_V09_PREVIEW
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Persistence;

public sealed class AuditIndexTests
{
    private static AuditIndex MakeSut(TempStorageFixture fix) =>
        new(TestData.Factory(fix.Options));

    [Fact]
    public async Task Empty_Count_IsZero()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        (await sut.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
    }

    [Fact]
    public async Task Upsert_ThenGet_ReturnsRow()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var row = new AuditIndexRow("t1", 1, DateTimeOffset.UtcNow, "mod", "forward");
        await sut.UpsertAsync(row, TestContext.Current.CancellationToken);
        var found = await sut.TryGetByTraceIdAsync("t1", TestContext.Current.CancellationToken);
        found.ShouldNotBeNull();
        found!.TraceId.ShouldBe("t1");
        found.Module.ShouldBe("mod");
    }

    [Fact]
    public async Task Upsert_Overwrite_UpdatesRow()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.UpsertAsync(new AuditIndexRow("t1", 1, ts, "mod", "forward"), TestContext.Current.CancellationToken);
        await sut.UpsertAsync(new AuditIndexRow("t1", 2, ts, "mod2", "stream"), TestContext.Current.CancellationToken);
        var found = await sut.TryGetByTraceIdAsync("t1", TestContext.Current.CancellationToken);
        found!.Sequence.ShouldBe(2L);
        found.Module.ShouldBe("mod2");
    }

    [Fact]
    public async Task TryGet_Missing_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var found = await sut.TryGetByTraceIdAsync("nope", TestContext.Current.CancellationToken);
        found.ShouldBeNull();
    }

    [Fact]
    public async Task Count_AfterUpsert_Increments()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.UpsertAsync(new AuditIndexRow("t1", 1, ts, "m", "e"), TestContext.Current.CancellationToken);
        await sut.UpsertAsync(new AuditIndexRow("t2", 2, ts, "m", "e"), TestContext.Current.CancellationToken);
        (await sut.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2L);
    }

    [Fact]
    public async Task DeleteAll_ClearsRows()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.UpsertAsync(new AuditIndexRow("t1", 1, ts, "m", "e"), TestContext.Current.CancellationToken);
        await sut.DeleteAllAsync(TestContext.Current.CancellationToken);
        (await sut.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
    }

    [Fact]
    public async Task TryGet_AfterDelete_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sut = MakeSut(fix);
        var ts = DateTimeOffset.UtcNow;
        await sut.UpsertAsync(new AuditIndexRow("t1", 1, ts, "m", "e"), TestContext.Current.CancellationToken);
        await sut.DeleteAllAsync(TestContext.Current.CancellationToken);
        (await sut.TryGetByTraceIdAsync("t1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
