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

    public static TheoryData<string, object?> NonStringValues => new()
    {
        { "long", 7L },
        { "long max", long.MaxValue },
        { "int", 5 },
        { "double", 2.5 },
        { "double that is a whole number", 3.0 },
        { "double with an exponent", 3e18 },
        { "bool", true },
        { "null", null },
        { "list", new List<object?> { "a", 1L, 2.5, false, null } },
        { "empty list", new List<object?>() },
        { "array", new object?[] { "a", 1L } },
        { "nested object", new Dictionary<string, object?> { ["k"] = "v", ["n"] = 1L, ["inner"] = new Dictionary<string, object?> { ["d"] = 0.5 } } },
        { "list of objects", new List<object?> { new Dictionary<string, object?> { ["k"] = true } } },
        { "negative zero", -0.0 },
        { "negative zero in a list", new List<object?> { -0.0 } },
        { "sbyte", (sbyte)-5 },
        { "byte", (byte)200 },
        { "short", (short)-300 },
        { "ushort", (ushort)60000 },
        { "uint", 4000000000u },
        { "long min", long.MinValue },
        { "ulong within long range", 5UL },
        { "ulong at long max", (ulong)long.MaxValue },
        { "float", 0.1f },
        { "float max", float.MaxValue },
        { "double 0.1", 0.1 },
        { "double 1e-7", 1e-7 },
        { "double 1e21", 1e21 },
        { "double max", double.MaxValue },
        { "double epsilon", double.Epsilon },
        { "double with many digits", 123456789.123456789 },
        { "double 0.1 + 0.2", 0.30000000000000004 },
        { "string array", new[] { "a", "b" } },
        { "list of strings", new List<string> { "a" } },
        { "int array", new[] { 1, 2, 3 } },
        { "string dictionary", new Dictionary<string, string> { ["k"] = "v" } },
        { "dictionary of ints", new Dictionary<string, int> { ["k"] = 1 } },
        { "read-only dictionary", new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["k"] = "v" }) },
        { "immutable array", System.Collections.Immutable.ImmutableArray.Create("a", "b") },
        { "list of mixed small numbers", new List<object?> { (byte)1, 0.1f, 1e-7 } },
        { "object holding a float", new Dictionary<string, object?> { ["k"] = 0.1f } },
    };

    public static TheoryData<string, object?> RejectedValues => new()
    {
        { "ulong above long max", 9223372036854775808UL },
        { "ulong max", ulong.MaxValue },
        { "decimal", 12.5m },
        { "char", 'c' },
        { "DateTime", new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc) },
        { "Guid", Guid.Empty },
        { "byte array", new byte[] { 1, 2 } },
        { "NaN", double.NaN },
        { "positive infinity", double.PositiveInfinity },
        { "negative infinity", double.NegativeInfinity },
        { "float NaN", float.NaN },
        { "dictionary with integer keys", new Dictionary<int, string> { [1] = "v" } },
        { "plain object", new object() },
        { "list holding a Guid", new List<object?> { "ok", Guid.Empty } },
        { "object holding a decimal", new Dictionary<string, object?> { ["k"] = 1m } },
    };

    [Theory]
    [MemberData(nameof(RejectedValues))]
    public async Task AppendAsync_RejectsOtherValues_BeforeAnythingIsWritten(string label, object? value)
    {
        var ts = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var ins = new Dictionary<string, object?> { ["v"] = value };
        var outs = new Dictionary<string, object?>();
        Should.Throw<AuditSerializationException>(
            () => AuditChainHasher.Compute(0, ts, "M", "m", ins, outs, 1, 2, AuditChainHasher.ZeroHash), label);
        var entry = new AuditEntry
        {
            Sequence = 0, Timestamp = ts, ModuleName = "M", ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = AuditChainHasher.ZeroHash, EntryHash = new byte[32],
        };

        await using var sink = new JsonFileAuditSink(_path);
        await Should.ThrowAsync<AuditSerializationException>(
            async () => await sink.AppendAsync(entry, TestContext.Current.CancellationToken), label);

        sink.NextSequence.ShouldBe(0L);
        new FileInfo(_path).Length.ShouldBe(0L);
    }

    [Theory]
    [InlineData("\n", 1)]
    [InlineData("garbage\n", 1)]
    [InlineData("{\"Sequence\":0 ,\n", 1)]
    [InlineData("{}\n", 1)]
    [InlineData("null\n", 1)]
    [InlineData("\n\n", 2)]
    public async Task FileWithAnUnreadableLastLine_StillOpens_ButARecorderRefusesToExtendIt(string content, int lines)
    {
        await File.WriteAllTextAsync(_path, content, TestContext.Current.CancellationToken);
        await using var sink = new JsonFileAuditSink(_path);
        sink.NextSequence.ShouldBe((long)lines);

        // Reading is as it always was: the damaged line fails to parse when it is reached.
        await Should.ThrowAsync<Exception>(
            async () => await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken));

        // Extending it is refused, naming the file and the line, and nothing is written.
        var hook = new AuditHook(sink);
        var ctx = new SignalGrid.Ampelum.Hooks.HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["q"] = "hi" },
        };
        ctx.Outputs = new Dictionary<string, object?> { ["a"] = "ok" };
        var refused = await Should.ThrowAsync<InvalidDataException>(
            async () => await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken));
        refused.Message.ShouldContain(_path);
        refused.Message.ShouldContain($"line {lines}");
        sink.NextSequence.ShouldBe((long)lines);
        new FileInfo(_path).Length.ShouldBe((long)content.Length);
    }

    [Fact]
    public async Task AuditHook_RecordsAFloat_AndTheFileVerifies()
    {
        await using var sink = new JsonFileAuditSink(_path);
        var hook = new AuditHook(sink);
        var ctx = new SignalGrid.Ampelum.Hooks.HookContext
        {
            ModuleName = "M",
            Inputs = new Dictionary<string, object?> { ["temperature"] = 0.7f, ["zero"] = -0.0 },
        };
        ctx.Outputs = new Dictionary<string, object?> { ["score"] = (byte)3 };

        await hook.OnAfterAsync(ctx, TestContext.Current.CancellationToken);

        var verdict = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        verdict.IsValid.ShouldBeTrue(verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(NonStringValues))]
    public async Task AppendAsync_WritesNonStringValues_ThatReadBackAndVerify(string label, object? value)
    {
        var ts = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var ins = new Dictionary<string, object?> { ["v"] = value };
        var outs = new Dictionary<string, object?> { ["w"] = value };
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
        verdict.IsValid.ShouldBeTrue($"{label}: {verdict.Reason}");
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
