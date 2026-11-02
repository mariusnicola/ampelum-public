using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class JsonFileAuditSinkTests : IAsyncLifetime
{
    string _path = "";

    public ValueTask InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"ampelum-audit-{Guid.NewGuid():N}.jsonl");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_path)) File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    static AuditEntry Make(long seq, byte[] prev)
    {
        var ts = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        return new()
        {
            Sequence = seq, Timestamp = ts, ModuleName = "M", ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = prev,
            EntryHash = AuditChainHasher.Compute(seq, ts, "M", "m", ins, outs, 1, 2, prev),
        };
    }

    [Fact]
    public async Task AppendAsync_PersistsEntries_RecoversNextSequence()
    {
        await using (var sink = new JsonFileAuditSink(_path))
        {
            sink.NextSequence.ShouldBe(0L);
            await sink.AppendAsync(Make(0, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken);
            sink.NextSequence.ShouldBe(1L);
        }

        await using var reopened = new JsonFileAuditSink(_path);
        reopened.NextSequence.ShouldBe(1L);
        var collected = new List<AuditEntry>();
        await foreach (var e in reopened.EnumerateAsync(TestContext.Current.CancellationToken))
            collected.Add(e);
        collected.Count.ShouldBe(1);
        collected[0].Sequence.ShouldBe(0L);
    }

    [Fact]
    public async Task AppendAsync_OutOfOrder_Throws()
    {
        await using var sink = new JsonFileAuditSink(_path);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sink.AppendAsync(Make(5, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendAsync_FsyncsAfterEachWrite()
    {
        await using var sink = new JsonFileAuditSink(_path);
        await sink.AppendAsync(Make(0, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken);
        // External reader must use FileShare.ReadWrite to coexist with the live writer handle.
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var content = await sr.ReadToEndAsync(TestContext.Current.CancellationToken);
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
    }
}
