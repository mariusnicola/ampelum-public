using System.Security.Cryptography;
using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using SignalGrid.Ampelum.Hooks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S6 — audit chain throughput.
///
/// Plan name: Scenario06_AuditChainThroughput (must-be-#1 per v1-decisions §B3).
///
/// Compares Ampelum's full hash-chained <see cref="AuditHook"/> + <see cref="InMemoryAuditSink"/>
/// path (the production pipeline a user gets out-of-the-box) against a
/// "build-from-scratch" naive chain: <see cref="JsonSerializer.SerializeToUtf8Bytes(object?, Type, JsonSerializerOptions?)"/>
/// + <see cref="SHA256.HashData(byte[])"/> per entry, plus a <see cref="List{T}"/> append.
/// Both rows include the LM call cost (<see cref="MockChatClient"/>) so the delta
/// is pure audit-machinery overhead per Predict.
///
/// <para>Honesty note (per advisor §S6):</para>
/// Ampelum's <c>AuditChainHasher</c> performs canonical sort + IncrementalHash + BigEndian
/// framing — strictly heavier per-entry than naive serialise+hash. The must-be-#1
/// claim therefore relies on amortisation via batched sinks rather than per-entry
/// compute. The numbers are reported as-is; if the naive baseline wins the
/// per-entry race, the gap is documented in alpha.6's ranking output and
/// future work moves to a <see cref="BatchedAuditSink"/>-based comparison.
/// </summary>
[MemoryDiagnoser]
public class Scenario06_AuditChainThroughput
{
    private MockChatClient _mock = null!;
    private Predict<QaSig> _ampelumWithAudit = null!;
    private List<NaiveAuditEntry> _naiveSink = null!;
    private byte[] _naivePrev = null!;

    // JsonSerializerOptions is the only mutable per-call state for the naive path;
    // pre-build it so the benchmark only measures the serialise+hash cost.
    private static readonly JsonSerializerOptions _naiveJson = new() { WriteIndented = false };

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        var sink = new InMemoryAuditSink();
        var ctx = new AmpelumContext(
                _mock,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink));
        _ampelumWithAudit = new Predict<QaSig>(ctx);

        _naiveSink = [];
        _naivePrev = new byte[32];
    }

    /// <summary>
    /// Reset the naive baseline state per BDN iteration so the
    /// <see cref="NaiveHandRolled"/> row sees a constant input on every call.
    /// Without this, <c>_naiveSink.Add(...)</c> grows the list across the
    /// thousands of invocations BDN runs per iteration: the list reallocates
    /// past capacity, the <c>Sequence</c> field grows unbounded, and the
    /// per-call JSON serialisation cost drifts upward — the fixture-leak
    /// risk flagged in <c>docs/performance/bench-harness-audit.md</c>
    /// (2026-04-26 audit, S6 row).
    /// </summary>
    [IterationSetup(Target = nameof(NaiveHandRolled))]
    public void ResetNaiveSink()
    {
        _naiveSink = [];
        _naivePrev = new byte[32];
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum()
        => await _ampelumWithAudit.ForwardAsync(new QaSig { Question = "q" });

    [Benchmark]
    public async Task<int> NaiveHandRolled()
    {
        // Equivalent shape: do the LM call, then build+hash an entry.
        var resp = await _mock.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "q")]);

        var entry = new NaiveAuditEntry(
            Sequence: _naiveSink.Count,
            ModuleName: "Predict",
            ModelId: "mock",
            Question: "q",
            Answer: resp.Text ?? "",
            PrevHash: _naivePrev);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, _naiveJson);
        // Mix in prev so chain is real.
        var combined = new byte[bytes.Length + 32];
        Buffer.BlockCopy(bytes, 0, combined, 0, bytes.Length);
        Buffer.BlockCopy(_naivePrev, 0, combined, bytes.Length, 32);
        var hash = SHA256.HashData(combined);
        _naivePrev = hash;
        _naiveSink.Add(entry with { EntryHash = hash });
        return _naiveSink.Count;
    }

    /// <summary>Hand-rolled audit entry shape used by the naive baseline.</summary>
    private sealed record NaiveAuditEntry(
        long Sequence,
        string ModuleName,
        string ModelId,
        string Question,
        string Answer,
        byte[] PrevHash)
    {
        public byte[]? EntryHash { get; init; }
    }
}
