using System.Diagnostics;
using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief A5 stress test — N-parallel scaling at platform-eng scales
/// (1K–10K concurrent forwards). Companion to <see cref="Scenario09_NParallelScaling"/>
/// which only stress-tested up to N=64 (D5 budget point).
///
/// <para>
/// Spec context: D5 budget commits only to N=64 (`p99 ≤1.5× baseline at N=64`).
/// A5 lists persona scale targets (enterprise-eng 100-10k, platform-eng 1k-100k)
/// but D5 does NOT pin a budget there. This scenario is therefore **informational**:
/// it verifies whether Ampelum's measured 4× over the harness floor (EmptyAsync
/// = `Task.Yield()` + WhenAll fan-in) holds flat as N grows, or whether
/// threadpool starvation / GC pressure / per-forward allocation inflates the
/// ratio at higher N.
/// </para>
///
/// <h3>Method</h3>
/// <para>
/// A single BDN invocation fans out N forwards via <c>WhenAll</c> and times
/// each individually with <see cref="Stopwatch.GetTimestamp"/>. The
/// per-forward latencies are sorted and reduced to p50/p95/p99 inside
/// <see cref="GlobalCleanup"/>. BDN ShortRun's mean/alloc remain the
/// per-batch figures (divide alloc by N for per-forward alloc).
/// </para>
///
/// <para>
/// We do NOT call <c>ThreadPool.SetMinThreads</c> — the goal is to surface
/// (not mask) any threadpool-ramp behaviour at high N.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 3, invocationCount: 1, launchCount: 1)]
public class Scenario09b_StressNParallelScaling
{
    /// <summary>N values targeting A5 platform-eng tier (1K-10K).</summary>
    [Params(64, 256, 1024, 4096, 10000)]
    public int Parallelism { get; set; }

    private YieldingMockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private QaSig _input = null!;
    private long[] _ampelumTicks = null!;
    private long[] _emptyTicks = null!;
    private static readonly double s_tickToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new YieldingMockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _ampelum = Predict.Create<QaSig>(_mock);
        _input = new QaSig { Question = "q" };
        _ampelumTicks = new long[Parallelism];
        _emptyTicks = new long[Parallelism];

        // Surface the threadpool defaults — printed once per [Params] cell.
        ThreadPool.GetMinThreads(out var minW, out var minIo);
        ThreadPool.GetMaxThreads(out var maxW, out var maxIo);
        Console.WriteLine($"[S9b setup] N={Parallelism} ProcessorCount={Environment.ProcessorCount} TP minW={minW} minIo={minIo} maxW={maxW} maxIo={maxIo}");
    }

    [Benchmark(Baseline = true)]
    public async Task Ampelum()
    {
        var n = Parallelism;
        var ticks = _ampelumTicks;
        var input = _input;
        var ampelum = _ampelum;
        var tasks = new Task[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            tasks[idx] = Run(idx);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task Run(int idx)
        {
            var start = Stopwatch.GetTimestamp();
            await ampelum.ForwardAsync(input).ConfigureAwait(false);
            ticks[idx] = Stopwatch.GetTimestamp() - start;
        }
    }

    [Benchmark]
    public async Task EmptyAsync()
    {
        var n = Parallelism;
        var ticks = _emptyTicks;
        var tasks = new Task[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            tasks[idx] = Run(idx);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task Run(int idx)
        {
            var start = Stopwatch.GetTimestamp();
            await Task.Yield();
            ticks[idx] = Stopwatch.GetTimestamp() - start;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        ReportPercentiles("Ampelum", _ampelumTicks);
        ReportPercentiles("EmptyAsync", _emptyTicks);
    }

    private void ReportPercentiles(string name, long[] ticks)
    {
        if (ticks.Length == 0 || ticks[0] == 0)
        {
            Console.WriteLine($"[S9b {name} N={Parallelism}] no samples captured");
            return;
        }

        var copy = (long[])ticks.Clone();
        Array.Sort(copy);

        double Pct(double p)
        {
            int idx = (int)Math.Ceiling(p * copy.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= copy.Length) idx = copy.Length - 1;
            return copy[idx] * s_tickToNs;
        }

        double mean = 0;
        for (int i = 0; i < copy.Length; i++) mean += copy[i] * s_tickToNs;
        mean /= copy.Length;

        Console.WriteLine(
            $"[S9b {name} N={Parallelism}] " +
            $"mean={mean:F0}ns p50={Pct(0.50):F0}ns p95={Pct(0.95):F0}ns " +
            $"p99={Pct(0.99):F0}ns max={copy[^1] * s_tickToNs:F0}ns " +
            $"samples={copy.Length}");
    }
}
