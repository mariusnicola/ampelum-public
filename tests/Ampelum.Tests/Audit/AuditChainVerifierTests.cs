using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditChainVerifierTests
{
    static readonly DateTimeOffset Ts = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    static AuditEntry Build(long seq, byte[] prev, string moduleName = "M")
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        return new()
        {
            Sequence = seq, Timestamp = Ts, ModuleName = moduleName, ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = prev,
            EntryHash = AuditChainHasher.Compute(seq, Ts, moduleName, "m", ins, outs, 1, 2, prev),
        };
    }

    [Fact]
    public async Task Verify_EmptySink_IsValid()
    {
        var sink = new InMemoryAuditSink();
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_GoodChain_IsValid()
    {
        var sink = new InMemoryAuditSink();
        var e0 = Build(0, AuditChainHasher.ZeroHash);
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        var e1 = Build(1, e0.EntryHash);
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
        r.BreakAtSequence.ShouldBeNull();
    }

    [Fact]
    public async Task Verify_TamperedEntryHash_DetectedAtBreakSequence()
    {
        var sink = new InMemoryAuditSink();
        await sink.AppendAsync(Build(0, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken);

        var snap = await Snapshot(sink);
        var tampered = Build(1, snap[0].EntryHash) with
        {
            EntryHash = new byte[32]
        };
        await sink.AppendAsync(tampered, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(1L);
        r.Reason.ShouldContain("EntryHash");
    }

    [Fact]
    public async Task Verify_PrevHashLinkBroken_Detected()
    {
        var sink = new InMemoryAuditSink();
        await sink.AppendAsync(Build(0, AuditChainHasher.ZeroHash), TestContext.Current.CancellationToken);
        await sink.AppendAsync(Build(1, new byte[32]), TestContext.Current.CancellationToken);
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(1L);
        r.Reason.ShouldContain("PrevHash");
    }

    [Fact]
    public async Task Verify_SequenceBreak_Detected()
    {
        // Sink that yields a single entry with sequence != expected (=0).
        var sink = new BogusSink(new[] { Build(5, AuditChainHasher.ZeroHash) });
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(5L);
        r.Reason.ShouldContain("Sequence break");
    }

    sealed class BogusSink : IAuditSink
    {
        readonly AuditEntry[] _items;
        public BogusSink(AuditEntry[] items) { _items = items; }
        public long NextSequence => _items.Length;
        public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct) => throw new NotSupportedException();
#pragma warning disable CS1998 // async w/o await: yields are sufficient
        public async IAsyncEnumerable<AuditEntry> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
#pragma warning restore CS1998
        {
            foreach (var e in _items) yield return e;
        }
    }

    [Fact]
    public async Task Verify_FirstEntryPrevHashNonZero_Detected()
    {
        var sink = new InMemoryAuditSink();
        var bogus = new byte[32]; bogus[0] = 1;
        await sink.AppendAsync(Build(0, bogus), TestContext.Current.CancellationToken);
        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(0L);
    }

    // v1.0-rc1.alpha.12: SchemaVersion-aware verification.

    static AuditEntry BuildV2(long seq, byte[] prev, string eventType = "forward.completed", string moduleName = "M")
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        return new()
        {
            SchemaVersion = 2,
            EventType = eventType,
            Sequence = seq, Timestamp = Ts, ModuleName = moduleName, ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = prev,
            EntryHash = AuditChainHasher.ComputeV2(seq, Ts, moduleName, eventType, "m", ins, outs, 1, 2, prev),
        };
    }

    [Fact]
    public async Task Verify_V2Row_TamperedEventType_DetectedByEntryHash()
    {
        var sink = new InMemoryAuditSink();
        var e0 = BuildV2(0, AuditChainHasher.ZeroHash, eventType: "agent.handoff");
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);

        // Mutate EventType after the fact, keeping EntryHash from the original event-type.
        // Using a BogusSink to inject the tampered row (InMemoryAuditSink is append-only).
        var tampered = e0 with { EventType = "router.failover" };
        var bogusSink = new BogusSink(new[] { tampered });

        var r = await AuditChainVerifier.VerifyAsync(bogusSink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(0L);
        r.Reason.ShouldContain("EntryHash");
    }

    [Fact]
    public async Task Verify_V1Row_NoEventTypeInHash_VerifiesAtDefault()
    {
        // Pre-alpha.12 row: SchemaVersion=1 (the C# default), EventType at default
        // "forward.completed", EntryHash computed via the v1 (legacy) algorithm.
        var sink = new InMemoryAuditSink();
        var e0 = Build(0, AuditChainHasher.ZeroHash); // v1 build helper
        e0.SchemaVersion.ShouldBe(1);
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_MixedV1AndV2Chain_Verifies()
    {
        // v1 row at sequence 0, v2 row at sequence 1 — each verifies under its own algo.
        var sink = new InMemoryAuditSink();
        var e0 = Build(0, AuditChainHasher.ZeroHash);
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        var e1 = BuildV2(1, e0.EntryHash, eventType: "agent.handoff");
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Verify_V2Row_GoodChain_Verifies()
    {
        var sink = new InMemoryAuditSink();
        var e0 = BuildV2(0, AuditChainHasher.ZeroHash, eventType: "agent.handoff");
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        var e1 = BuildV2(1, e0.EntryHash, eventType: "router.failover");
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    // v1.0-rc1.alpha.13: SchemaVersion=3 verification.

    static AuditEntry BuildV3(long seq, byte[] prev, string eventType = "forward.completed",
        string traceId = "deadbeefdeadbeefdeadbeefdeadbeef", string moduleName = "M")
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        return new()
        {
            SchemaVersion = 3,
            EventType = eventType,
            TraceId = traceId,
            Sequence = seq, Timestamp = Ts, ModuleName = moduleName, ModelId = "m",
            Inputs = ins, Outputs = outs, PromptTokens = 1, CompletionTokens = 2,
            PrevHash = prev,
            EntryHash = AuditChainHasher.ComputeV3(seq, Ts, moduleName, eventType, "m", ins, outs, 1, 2, traceId, prev),
        };
    }

    [Fact]
    public async Task Verify_V3Row_GoodChain_Verifies()
    {
        var sink = new InMemoryAuditSink();
        var e0 = BuildV3(0, AuditChainHasher.ZeroHash, traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        var e1 = BuildV3(1, e0.EntryHash, traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
        r.BreakAtSequence.ShouldBeNull();
    }

    [Fact]
    public async Task Verify_V3Row_TamperedTraceId_DetectedByEntryHash()
    {
        var e0 = BuildV3(0, AuditChainHasher.ZeroHash, traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        // Tamper: replace TraceId but keep the original EntryHash.
        var tampered = e0 with { TraceId = "cccccccccccccccccccccccccccccccc" };
        var bogusSink = new BogusSink(new[] { tampered });

        var r = await AuditChainVerifier.VerifyAsync(bogusSink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeFalse();
        r.BreakAtSequence.ShouldBe(0L);
        r.Reason.ShouldContain("EntryHash");
    }

    [Fact]
    public async Task Verify_MixedV1V2V3Chain_Verifies()
    {
        var sink = new InMemoryAuditSink();
        var e0 = Build(0, AuditChainHasher.ZeroHash);
        await sink.AppendAsync(e0, TestContext.Current.CancellationToken);
        var e1 = BuildV2(1, e0.EntryHash, eventType: "agent.handoff");
        await sink.AppendAsync(e1, TestContext.Current.CancellationToken);
        var e2 = BuildV3(2, e1.EntryHash, traceId: "deadbeefdeadbeefdeadbeefdeadbeef");
        await sink.AppendAsync(e2, TestContext.Current.CancellationToken);

        var r = await AuditChainVerifier.VerifyAsync(sink, TestContext.Current.CancellationToken);
        r.IsValid.ShouldBeTrue();
    }

    static async Task<AuditEntry[]> Snapshot(IAuditSink sink)
    {
        var list = new List<AuditEntry>();
        await foreach (var e in sink.EnumerateAsync(TestContext.Current.CancellationToken))
            list.Add(e);
        return list.ToArray();
    }
}
