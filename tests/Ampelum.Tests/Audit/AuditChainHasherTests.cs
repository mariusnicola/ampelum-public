using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SignalGrid.Ampelum.Audit;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Audit;

public sealed class AuditChainHasherTests
{
    static readonly DateTimeOffset Ts = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ZeroHash_Is32Bytes_AllZeros()
    {
        AuditChainHasher.ZeroHash.Length.ShouldBe(32);
        AuditChainHasher.ZeroHash.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void ZeroHash_DefensivelyCopied_BetweenCalls()
    {
        var a = AuditChainHasher.ZeroHash;
        a[0] = 0xFF;
        AuditChainHasher.ZeroHash[0].ShouldBe((byte)0);
    }

    [Fact]
    public void Compute_ProducesDeterministicHash_GivenSameInputs()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi", ["n"] = 1L };
        var outs = new Dictionary<string, object?> { ["a"] = "hello" };
        var prev = AuditChainHasher.ZeroHash;
        var h1 = AuditChainHasher.Compute(0, Ts, "M", "model", ins, outs, 1, 2, prev);
        var h2 = AuditChainHasher.Compute(0, Ts, "M", "model", ins, outs, 1, 2, prev);
        h1.ShouldBe(h2);
        h1.Length.ShouldBe(32);
    }

    [Fact]
    public void Compute_DiffersWhenAnyFieldChanges()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var baseline = AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 1, 2, prev);

        AuditChainHasher.Compute(1, Ts, "M", "m", ins, outs, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts.AddSeconds(1), "M", "m", ins, outs, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "N", "m", ins, outs, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "M", "n", ins, outs, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "M", "m", new Dictionary<string,object?>{["q"]="x"}, outs, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "M", "m", ins, new Dictionary<string,object?>{["a"]="x"}, 1, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 9, 2, prev).ShouldNotBe(baseline);
        AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 1, 9, prev).ShouldNotBe(baseline);
        var prev2 = new byte[32]; prev2[0] = 1;
        AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 1, 2, prev2).ShouldNotBe(baseline);
    }

    [Fact]
    public void Compute_KeyOrderInIputsDoesNotAffectHash()
    {
        var a = new Dictionary<string, object?> { ["a"] = 1L, ["b"] = 2L };
        var b = new Dictionary<string, object?> { ["b"] = 2L, ["a"] = 1L };
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        AuditChainHasher.Compute(0, Ts, "M", "m", a, outs, 0, 0, prev)
            .ShouldBe(AuditChainHasher.Compute(0, Ts, "M", "m", b, outs, 0, 0, prev));
    }

    [Fact]
    public void Compute_NonPrimitive_ThrowsAuditSerializationException()
    {
        var ins = new Dictionary<string, object?> { ["bad"] = new { X = 1 } };
        var outs = new Dictionary<string, object?>();
        Should.Throw<AuditSerializationException>(() =>
            AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 0, 0, AuditChainHasher.ZeroHash));
    }

    [Fact]
    public void Compute_PrevHashWrongLength_Throws()
    {
        var ins = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        Should.Throw<ArgumentException>(() =>
            AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 0, 0, new byte[16]));
    }

    [Fact]
    public void Compute_NestedDictAndList_AreCanonicalised()
    {
        var ins = new Dictionary<string, object?>
        {
            ["o"] = new Dictionary<string, object?> { ["k"] = "v", ["i"] = 1 },
            ["a"] = new List<object?> { 1L, "x" }
        };
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var h = AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 0, 0, prev);
        h.Length.ShouldBe(32);

        // Same content, different dict ordering -> same hash (canonicalisation).
        var insB = new Dictionary<string, object?>
        {
            ["a"] = new List<object?> { 1L, "x" },
            ["o"] = new Dictionary<string, object?> { ["i"] = 1, ["k"] = "v" }
        };
        AuditChainHasher.Compute(0, Ts, "M", "m", insB, outs, 0, 0, prev).ShouldBe(h);
    }

    [Fact]
    public void ComputeV2_DiffersFromV1_AndDetectsEventTypeChange()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var v1 = AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 1, 2, prev);
        var v2 = AuditChainHasher.ComputeV2(0, Ts, "M", "forward.completed", "m", ins, outs, 1, 2, prev);
        v2.ShouldNotBe(v1);

        // EventType change must alter the v2 hash.
        var v2b = AuditChainHasher.ComputeV2(0, Ts, "M", "agent.handoff", "m", ins, outs, 1, 2, prev);
        v2b.ShouldNotBe(v2);
    }

    [Fact]
    public void ComputeV2_PrevHashWrongLength_Throws()
    {
        var ins = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        Should.Throw<ArgumentException>(() =>
            AuditChainHasher.ComputeV2(0, Ts, "M", "e", "m", ins, outs, 0, 0, new byte[16]));
    }

    [Fact]
    public void ComputeV2_NonPrimitive_ThrowsAuditSerializationException()
    {
        var ins = new Dictionary<string, object?> { ["bad"] = new { X = 1 } };
        var outs = new Dictionary<string, object?>();
        Should.Throw<AuditSerializationException>(() =>
            AuditChainHasher.ComputeV2(0, Ts, "M", "e", "m", ins, outs, 0, 0, AuditChainHasher.ZeroHash));
    }

    [Fact]
    public void ComputeV2_Deterministic_GivenSameInputs()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var h1 = AuditChainHasher.ComputeV2(0, Ts, "M", "agent.handoff", "m", ins, outs, 1, 2, prev);
        var h2 = AuditChainHasher.ComputeV2(0, Ts, "M", "agent.handoff", "m", ins, outs, 1, 2, prev);
        h1.ShouldBe(h2);
        h1.Length.ShouldBe(32);
    }

    [Fact]
    public void ComputeV3_Deterministic_GivenSameInputs()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var h1 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 1, 2, "abcdef01abcdef01abcdef01abcdef01", prev);
        var h2 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 1, 2, "abcdef01abcdef01abcdef01abcdef01", prev);
        h1.ShouldBe(h2);
        h1.Length.ShouldBe(32);
    }

    [Fact]
    public void ComputeV3_DiffersFromV2_WhenTraceIdPresent()
    {
        var ins  = new Dictionary<string, object?> { ["q"] = "hi" };
        var outs = new Dictionary<string, object?> { ["a"] = "ok" };
        var prev = AuditChainHasher.ZeroHash;
        var v2 = AuditChainHasher.ComputeV2(0, Ts, "M", "forward.completed", "m", ins, outs, 1, 2, prev);
        var v3 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 1, 2, "abcdef01abcdef01abcdef01abcdef01", prev);
        v3.ShouldNotBe(v2);
    }

    [Fact]
    public void ComputeV3_WithEmptyTraceId_DiffersFromV2()
    {
        // Even empty traceId produces a different hash from V2, because the NUL sentinel is
        // always appended in V3 before the (zero-length) traceId bytes.
        var ins  = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var v2 = AuditChainHasher.ComputeV2(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, prev);
        var v3 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, "", prev);
        v3.ShouldNotBe(v2);
    }

    [Fact]
    public void ComputeV3_DiffersWhenTraceIdChanges()
    {
        var ins  = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var h1 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", prev);
        var h2 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", prev);
        h1.ShouldNotBe(h2);
    }

    [Fact]
    public void ComputeV3_DiffersWhenEventTypeChanges()
    {
        var ins  = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var tid  = "deadbeefdeadbeefdeadbeefdeadbeef";
        var h1 = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, tid, prev);
        var h2 = AuditChainHasher.ComputeV3(0, Ts, "M", "agent.handoff", "m", ins, outs, 0, 0, tid, prev);
        h1.ShouldNotBe(h2);
    }

    [Fact]
    public void ComputeV3_PrevHashWrongLength_Throws()
    {
        var ins = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        Should.Throw<ArgumentException>(() =>
            AuditChainHasher.ComputeV3(0, Ts, "M", "e", "m", ins, outs, 0, 0, "", new byte[16]));
    }

    [Fact]
    public void ComputeV3_NonPrimitive_ThrowsAuditSerializationException()
    {
        var ins = new Dictionary<string, object?> { ["bad"] = new { X = 1 } };
        var outs = new Dictionary<string, object?>();
        Should.Throw<AuditSerializationException>(() =>
            AuditChainHasher.ComputeV3(0, Ts, "M", "e", "m", ins, outs, 0, 0, "", AuditChainHasher.ZeroHash));
    }

    [Fact]
    public void ComputeV3_BigEndianLayout_VerifiableByHand()
    {
        var ins  = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var tid  = "deadbeefdeadbeef";
        var actual = AuditChainHasher.ComputeV3(0, Ts, "M", "forward.completed", "m", ins, outs, 0, 0, tid, prev);

        Span<byte> seq   = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(seq, 0);
        Span<byte> ticks = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(ticks, Ts.Ticks);
        Span<byte> pt    = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(pt, 0);
        Span<byte> ct    = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(ct, 0);
        var nul       = new byte[] { 0 };
        var moduleB   = Encoding.UTF8.GetBytes("M");
        var eventB    = Encoding.UTF8.GetBytes("forward.completed");
        var modelB    = Encoding.UTF8.GetBytes("m");
        var emptyJson = Encoding.UTF8.GetBytes("{}");
        var traceB    = Encoding.UTF8.GetBytes(tid);

        using var sha = SHA256.Create();
        sha.TransformBlock(seq.ToArray(),   0, 8, null, 0);
        sha.TransformBlock(ticks.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(moduleB, 0, moduleB.Length, null, 0);
        sha.TransformBlock(nul,     0, 1, null, 0);
        sha.TransformBlock(eventB,  0, eventB.Length,  null, 0);
        sha.TransformBlock(nul,     0, 1, null, 0);
        sha.TransformBlock(modelB,  0, modelB.Length,  null, 0);
        sha.TransformBlock(emptyJson, 0, emptyJson.Length, null, 0);
        sha.TransformBlock(emptyJson, 0, emptyJson.Length, null, 0);
        sha.TransformBlock(pt.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(ct.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(nul,     0, 1, null, 0);
        sha.TransformBlock(traceB,  0, traceB.Length, null, 0);
        sha.TransformFinalBlock(prev, 0, 32);

        actual.ShouldBe(sha.Hash!);
    }

    [Fact]
    public void Compute_BigEndianLayout_VerifiableByHand()
    {
        var ins = new Dictionary<string, object?>();
        var outs = new Dictionary<string, object?>();
        var prev = AuditChainHasher.ZeroHash;
        var actual = AuditChainHasher.Compute(0, Ts, "M", "m", ins, outs, 0, 0, prev);

        Span<byte> seq = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(seq, 0);
        Span<byte> ticks = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(ticks, Ts.Ticks);
        Span<byte> pt = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(pt, 0);
        Span<byte> ct = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(ct, 0);
        var moduleBytes = Encoding.UTF8.GetBytes("M");
        var modelBytes  = Encoding.UTF8.GetBytes("m");
        var emptyJson   = Encoding.UTF8.GetBytes("{}");

        using var sha = SHA256.Create();
        sha.TransformBlock(seq.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(ticks.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(moduleBytes, 0, moduleBytes.Length, null, 0);
        sha.TransformBlock(modelBytes,  0, modelBytes.Length,  null, 0);
        sha.TransformBlock(emptyJson, 0, emptyJson.Length, null, 0);
        sha.TransformBlock(emptyJson, 0, emptyJson.Length, null, 0);
        sha.TransformBlock(pt.ToArray(), 0, 8, null, 0);
        sha.TransformBlock(ct.ToArray(), 0, 8, null, 0);
        sha.TransformFinalBlock(prev, 0, 32);

        actual.ShouldBe(sha.Hash!);
    }
}
