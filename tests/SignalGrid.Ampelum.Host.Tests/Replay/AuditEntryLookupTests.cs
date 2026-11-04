#pragma warning disable AMPELUM_V09_PREVIEW
using SignalGrid.Ampelum.Audit;
using Shouldly;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Replay;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;

namespace SignalGrid.Ampelum.Host.Tests.Replay;

public sealed class AuditEntryLookupTests
{
    private static AuditEntryLookup MakeSut(IAuditSink sink, IAuditIndex index) =>
        new(sink, index);

    private static AuditEntry MakeEntry(long sequence, string traceId, Dictionary<string, object?>? inputs = null) =>
        new()
        {
            Sequence = sequence,
            TraceId = traceId,
            Timestamp = DateTimeOffset.UtcNow,
            ModuleName = "test-module",
            ModelId = "test-model",
            EventType = "forward.completed",
            Inputs = inputs ?? new Dictionary<string, object?>(),
            Outputs = new Dictionary<string, object?>(),
            PromptTokens = 0,
            CompletionTokens = 0,
            PrevHash = new byte[32],
            EntryHash = new byte[32],
        };

    [Fact]
    public async Task Empty_TryGetByTraceIdAsync_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        var result = await sut.TryGetByTraceIdAsync("unknown", TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Empty_TryGetBySequenceAsync_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        var result = await sut.TryGetBySequenceAsync(42, TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task WithEntry_TryGetByTraceIdAsync_ReturnsEntry()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Write an entry to the sink
        var entry = MakeEntry(0, "tr-123");
        await sink.AppendAsync(entry, TestContext.Current.CancellationToken);

        // Index the entry
        var indexRow = new AuditIndexRow("tr-123", 0, DateTimeOffset.UtcNow, "test-module", "forward.completed");
        await index.UpsertAsync(indexRow, TestContext.Current.CancellationToken);

        // Lookup by trace ID
        var result = await sut.TryGetByTraceIdAsync("tr-123", TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result!.TraceId.ShouldBe("tr-123");
        result.Sequence.ShouldBe(0L);
        result.ModuleName.ShouldBe("test-module");
    }

    [Fact]
    public async Task WithEntry_TryGetBySequenceAsync_ReturnsEntry()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Write an entry to the sink
        var entry = MakeEntry(0, "tr-456");
        await sink.AppendAsync(entry, TestContext.Current.CancellationToken);

        // Lookup by sequence
        var result = await sut.TryGetBySequenceAsync(0, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result!.Sequence.ShouldBe(0L);
        result.TraceId.ShouldBe("tr-456");
    }

    [Fact]
    public async Task IndexMiss_TryGetByTraceIdAsync_FindsTheEntryInTheSink()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Written to the sink but NOT to the index
        await sink.AppendAsync(MakeEntry(0, "tr-other"), TestContext.Current.CancellationToken);
        await sink.AppendAsync(MakeEntry(1, "tr-789"), TestContext.Current.CancellationToken);

        var result = await sut.TryGetByTraceIdAsync("tr-789", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Sequence.ShouldBe(1L);
        result.TraceId.ShouldBe("tr-789");
        (await index.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
    }

    [Fact]
    public async Task IndexMiss_TryGetByTraceIdAsync_UsesTheTraceIdTheHostRecordedInTheInputs()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var sut = MakeSut(sink, new AuditIndex(TestData.Factory(fix.Options)));
        var entry = MakeEntry(0, "", new Dictionary<string, object?> { ["_host.trace_id"] = "tr-from-inputs" });
        await sink.AppendAsync(entry, TestContext.Current.CancellationToken);

        var result = await sut.TryGetByTraceIdAsync("tr-from-inputs", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Sequence.ShouldBe(0L);
    }

    [Fact]
    public async Task UnknownId_TryGetByTraceIdAsync_ReturnsNull_ThoughtTheSinkHoldsOtherEntries()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);
        await sink.AppendAsync(MakeEntry(0, "tr-known"), TestContext.Current.CancellationToken);
        await index.UpsertAsync(new AuditIndexRow("tr-known", 0, DateTimeOffset.UtcNow, "test-module", "forward.completed"),
            TestContext.Current.CancellationToken);

        var result = await sut.TryGetByTraceIdAsync("tr-unknown", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task StaleIndexRow_TryGetByTraceIdAsync_ReturnsNull_AndDoesNotSearchTheSink()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);
        // The index points at sequence 7, which the sink does not hold; the entry itself is at sequence 0.
        await sink.AppendAsync(MakeEntry(0, "tr-stale"), TestContext.Current.CancellationToken);
        await index.UpsertAsync(new AuditIndexRow("tr-stale", 7, DateTimeOffset.UtcNow, "test-module", "forward.completed"),
            TestContext.Current.CancellationToken);

        var result = await sut.TryGetByTraceIdAsync("tr-stale", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SequenceMiss_TryGetBySequenceAsync_ReturnsNull()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Write an entry at sequence 0
        var entry = MakeEntry(0, "tr-abc");
        await sink.AppendAsync(entry, TestContext.Current.CancellationToken);

        // Try to get a different sequence
        var result = await sut.TryGetBySequenceAsync(999, TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task MultipleEntries_TryGetBySequenceAsync_ReturnsCorrectOne()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Write multiple entries
        var entry1 = MakeEntry(0, "tr-1");
        var entry2 = MakeEntry(1, "tr-2");
        var entry3 = MakeEntry(2, "tr-3");

        await sink.AppendAsync(entry1, TestContext.Current.CancellationToken);
        await sink.AppendAsync(entry2, TestContext.Current.CancellationToken);
        await sink.AppendAsync(entry3, TestContext.Current.CancellationToken);

        // Lookup middle entry
        var result = await sut.TryGetBySequenceAsync(1, TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result!.Sequence.ShouldBe(1L);
        result.TraceId.ShouldBe("tr-2");
    }

    [Fact]
    public async Task MultipleEntries_TryGetByTraceIdAsync_ReturnsCorrectOne()
    {
        using var fix = new TempStorageFixture();
        var sink = new InMemoryAuditSink();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        var sut = MakeSut(sink, index);

        // Write multiple entries
        var entry1 = MakeEntry(0, "tr-alpha");
        var entry2 = MakeEntry(1, "tr-beta");
        var entry3 = MakeEntry(2, "tr-gamma");

        await sink.AppendAsync(entry1, TestContext.Current.CancellationToken);
        await sink.AppendAsync(entry2, TestContext.Current.CancellationToken);
        await sink.AppendAsync(entry3, TestContext.Current.CancellationToken);

        // Index only the second entry
        var indexRow = new AuditIndexRow("tr-beta", 1, DateTimeOffset.UtcNow, "test-module", "forward.completed");
        await index.UpsertAsync(indexRow, TestContext.Current.CancellationToken);

        // Lookup by trace ID
        var result = await sut.TryGetByTraceIdAsync("tr-beta", TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result!.Sequence.ShouldBe(1L);
        result.TraceId.ShouldBe("tr-beta");
    }

    [Fact]
    public async Task Constructor_NullSink_Throws()
    {
        using var fix = new TempStorageFixture();
        var index = new AuditIndex(TestData.Factory(fix.Options));
        Should.Throw<ArgumentNullException>(() => new AuditEntryLookup(null!, index));
    }

    [Fact]
    public async Task Constructor_NullIndex_Throws()
    {
        var sink = new InMemoryAuditSink();
        Should.Throw<ArgumentNullException>(() => new AuditEntryLookup(sink, null!));
    }
}
