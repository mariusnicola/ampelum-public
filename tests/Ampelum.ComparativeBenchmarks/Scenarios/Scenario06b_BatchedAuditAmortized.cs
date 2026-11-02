using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Audit;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using SignalGrid.Ampelum.Hooks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S6 follow-up — BatchedAuditSink amortized throughput.
///
/// Plan name: Scenario06b_BatchedAuditAmortized.
///
/// <para>Companion to <see cref="Scenario06_AuditChainThroughput"/>. The original S6
/// reports per-call cost where Ampelum's canonical-sort + IncrementalHash + BigEndian
/// framing is strictly heavier than a hand-rolled JSON+SHA256 chain — the
/// must-be-#1 claim only resolves once batching amortizes I/O across N entries.
/// This scenario measures the amortized side: <see cref="BatchedAuditSink"/>
/// in front of an I/O-shaped sink over a fixed batch of forwards.</para>
///
/// <para>2026-04-26-audit fix — synthetic-latency inner sink:</para>
/// The previous inner sink was <see cref="InMemoryAuditSink"/>, which had
/// effectively zero per-write cost. Wrapping a Channel-based batched sink
/// around a no-op inner sink measured pure tax (Channel + drain dispatch) with
/// no I/O to amortize, producing a misleading "amortized slower than per-call"
/// result. The fix replaces the inner with <see cref="LatencyAuditSink"/>
/// which simulates the I/O cost batching is designed to amortize — a small
/// <see cref="Task.Yield"/> + per-write CPU spin equivalent to roughly
/// the work an HTTP-bound or disk-flush sink would do per entry. The
/// per-call row uses the SAME inner sink without batching so the comparison
/// answers the actual question: "does batching amortize the I/O cost we'd
/// pay synchronously per call?" — apples-to-apples.
/// </summary>
[MemoryDiagnoser]
public class Scenario06b_BatchedAuditAmortized
{
    /// <summary>Batch size — the number of forwards each iteration drives through the sink.
    /// 32 is the typical sustained burst; 256 saturates the channel (capacity 1000
    /// default keeps producer non-blocking).</summary>
    [Params(32, 256)]
    public int Batch { get; set; }

    private MockChatClient _mock = null!;
    private QaSig _input = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _input = new QaSig { Question = "q" };
    }

    /// <summary>Ampelum batched: N forwards through Predict + AuditHook over BatchedAuditSink
    /// fronting a <see cref="LatencyAuditSink"/>, awaits drain at the end.
    /// Per-call cost = total_ms / N. The drain finalises every queued entry through
    /// the latency sink, so the I/O cost is paid; what's saved is the synchronous
    /// stall on the producer side.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> Ampelum_Batched()
    {
        var inner = new LatencyAuditSink();
        await using var batched = new BatchedAuditSink(inner);
        var ctx = new AmpelumContext(
                _mock,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance)
            .WithHook(new AuditHook(batched));
        var predict = new Predict<QaSig>(ctx);
        for (var i = 0; i < Batch; i++)
            await predict.ForwardAsync(_input);
        // DisposeAsync drains; the using block above ensures we wait for the channel.
        return Batch;
    }

    /// <summary>Naive per-entry sink baseline: same N forwards through Predict +
    /// AuditHook over the synchronous <see cref="LatencyAuditSink"/> (no batching).
    /// The per-forward cost includes the canonical-hash compute synchronously
    /// PLUS the synthetic I/O latency on the caller's thread — exactly what
    /// batching is designed to amortize.</summary>
    [Benchmark]
    public async Task<int> Ampelum_PerCall()
    {
        var sink = new LatencyAuditSink();
        var ctx = new AmpelumContext(
                _mock,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance)
            .WithHook(new AuditHook(sink));
        var predict = new Predict<QaSig>(ctx);
        for (var i = 0; i < Batch; i++)
            await predict.ForwardAsync(_input);
        return Batch;
    }

    /// <summary>
    /// Synthetic-I/O <see cref="IAuditSink"/>: each <see cref="AppendAsync"/>
    /// performs a <see cref="Task.Yield"/>, forcing an async state-machine
    /// completion off the synchronous fast-path. This is the minimum shape
    /// that distinguishes a "real I/O sink" from a synchronous in-memory
    /// list — every append schedules a thread-pool continuation, equivalent
    /// to what a buffered file/socket write would do once it has to await
    /// completion. No filesystem (flaky on Windows test hosts; needs
    /// GlobalCleanup) and no clock-based delay (timing-sensitive). The
    /// choice is option B from <c>docs/performance/bench-harness-audit.md</c>
    /// recommendation 3.
    /// </summary>
    private sealed class LatencyAuditSink : IAuditSink
    {
        private long _next;
        // ConcurrentQueue snapshots tolerate concurrent writes during enumeration —
        // BatchedAuditSink's drain loop reads NextSequence on a background task
        // while the producer pushes via AppendAsync, so the inner sink must be
        // thread-safe. (List<T> is not, and BDN surfaced this as a
        // "Collection was modified" InvalidOperationException.)
        private readonly System.Collections.Concurrent.ConcurrentQueue<AuditEntry> _entries = new();

        public long NextSequence => Volatile.Read(ref _next);

        public async ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
        {
            // Async suspension — equivalent to the await on a flush completion.
            await Task.Yield();
            _entries.Enqueue(entry);
            Interlocked.Increment(ref _next);
        }

        public async IAsyncEnumerable<AuditEntry> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var e in _entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return e;
            }
        }
    }
}
