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
    public async Task EnumerateAsync_ReturnsPlainValues_SoTheFileVerifiesThroughItsOwnSink()
    {
        var ts = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var ins = new Dictionary<string, object?>
        {
            ["text"] = "hi",
            ["more"] = "there",
        };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var entry = new AuditEntry
        {
            Sequence = 0, Timestamp = ts, ModuleName = "M", ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = AuditChainHasher.ZeroHash,
            EntryHash = AuditChainHasher.Compute(0, ts, "M", "m", ins, outs, 1, 2, AuditChainHasher.ZeroHash),
        };

        await using var sink = new JsonFileAuditSink(_path);
        await sink.AppendAsync(entry, TestContext.Current.CancellationToken);

        var verdict = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        verdict.IsValid.ShouldBeTrue(verdict.Reason);

        var read = await sink.EnumerateAsync(TestContext.Current.CancellationToken).FirstAsync(TestContext.Current.CancellationToken);
        read.Inputs["text"].ShouldBe("hi");
        read.Inputs["more"].ShouldBe("there");
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsPlainLists_AndObjects_FromALineWrittenByAnotherWriter()
    {
        var ts = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var ins = new Dictionary<string, object?>
        {
            ["whole"] = 7L,
            ["fraction"] = 2.5,
            ["flag"] = true,
            ["none"] = null,
            ["list"] = new List<object?> { "a", 1L, new Dictionary<string, object?> { ["k"] = false }, null },
            ["nested"] = new Dictionary<string, object?> { ["deep"] = "x" },
        };
        var outs = new Dictionary<string, object?>();
        var hash = AuditChainHasher.Compute(0, ts, "M", "m", ins, outs, 1, 2, AuditChainHasher.ZeroHash);
        var line = "{\"Sequence\":0,\"Timestamp\":\"2026-04-25T12:00:00+00:00\",\"ModuleName\":\"M\",\"ModelId\":\"m\","
            + "\"Inputs\":{\"whole\":7,\"fraction\":2.5,\"flag\":true,\"none\":null,\"list\":[\"a\",1,{\"k\":false},null],\"nested\":{\"deep\":\"x\"}},\"Outputs\":{},"
            + "\"PromptTokens\":1,\"CompletionTokens\":2,"
            + $"\"PrevHash\":\"{Convert.ToBase64String(AuditChainHasher.ZeroHash)}\",\"EntryHash\":\"{Convert.ToBase64String(hash)}\"}}\n";
        await File.WriteAllTextAsync(_path, line, TestContext.Current.CancellationToken);

        await using var sink = new JsonFileAuditSink(_path);

        var verdict = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        verdict.IsValid.ShouldBeTrue(verdict.Reason);
        var read = await sink.EnumerateAsync(TestContext.Current.CancellationToken).FirstAsync(TestContext.Current.CancellationToken);
        read.Inputs["whole"].ShouldBe(7L);
        read.Inputs["fraction"].ShouldBe(2.5);
        read.Inputs["flag"].ShouldBe(true);
        read.Inputs["none"].ShouldBeNull();
        var list = read.Inputs["list"].ShouldBeAssignableTo<IReadOnlyList<object?>>()!;
        list[0].ShouldBe("a");
        list[1].ShouldBe(1L);
        list[3].ShouldBeNull();
        list[2].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["k"].ShouldBe(false);
        read.Inputs["nested"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["deep"].ShouldBe("x");
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
