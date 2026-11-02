using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class InMemoryAuditSinkTests
{
    static AuditEntry MakeEntry(long seq, byte[] prev) => new()
    {
        Sequence = seq,
        Timestamp = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
        ModuleName = "M",
        ModelId = "m",
        Inputs = new Dictionary<string, object?> { ["q"] = "hi" },
        Outputs = new Dictionary<string, object?> { ["a"] = "ok" },
        PromptTokens = 1,
        CompletionTokens = 2,
        PrevHash = prev,
        EntryHash = AuditChainHasher.Compute(seq,
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
            "M", "m",
            new Dictionary<string, object?> { ["q"] = "hi" },
            new Dictionary<string, object?> { ["a"] = "ok" },
            1, 2, prev),
    };

    [Fact]
    public async Task NextSequence_StartsAtZero_AndAdvances()
    {
        var sink = new InMemoryAuditSink();
        sink.NextSequence.ShouldBe(0L);
        await sink.AppendAsync(MakeEntry(0, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken);
        sink.NextSequence.ShouldBe(1L);
    }

    [Fact]
    public async Task AppendAsync_StoresEntries_InOrder()
    {
        var sink = new InMemoryAuditSink();
        var e0 = MakeEntry(0, AuditChainHasher.ZeroHash);
        var e1 = MakeEntry(1, e0.EntryHash);
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);
        var collected = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken))
            collected.Add(e);
        collected.Count.ShouldBe(2);
        collected[0].Sequence.ShouldBe(0L);
        collected[1].Sequence.ShouldBe(1L);
    }

    [Fact]
    public async Task AppendAsync_OutOfOrderSequence_Throws()
    {
        var sink = new InMemoryAuditSink();
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sink.AppendAsync(MakeEntry(1, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("expected sequence 0");
    }
}
