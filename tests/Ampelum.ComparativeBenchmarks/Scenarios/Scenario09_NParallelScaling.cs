using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using BenchmarkDotNet.Attributes;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S9 — N-parallel scaling (reframed for v1.0-rc1).
///
/// <para>
/// Plan name: Scenario09_NParallelScaling. Original budget D5 (linear scaling
/// to <c>Environment.ProcessorCount</c>; p99 ≤1.5× baseline at N=64) was
/// founded on a flawed bench design that could not measure what it claimed.
/// </para>
///
/// <h3>What was wrong</h3>
/// <para>
/// The previous version of this benchmark used <see cref="MockChatClient"/>,
/// whose <c>GetResponseAsync</c> returns <see cref="Task.FromResult{T}"/>. On a
/// fan-out shape (<c>for i: tasks[i] = ForwardAsync(); WhenAll(tasks);</c>),
/// every <c>await</c> in <see cref="Predict{TSig}.ForwardAsync"/> sees an
/// already-completed task and continues synchronously on the caller's thread.
/// All N "parallel" forwards therefore run sequentially on one thread —
/// wall-clock scales linearly with N because there is no parallelism to
/// measure. The reported 64.57× at N=64 is N sequential forwards, not lock
/// contention.
/// </para>
///
/// <h3>Reframe</h3>
/// <para>
/// This version (a) swaps in <see cref="YieldingMockChatClient"/> so the
/// LM call has a real async suspension point and forwards actually run on
/// threadpool workers; and (b) adds a permanent <c>EmptyAsync</c> probe whose
/// only work is <c>Task.Yield()</c> per task. <c>EmptyAsync</c> is the
/// <em>physical floor</em> imposed by the threadpool/<see cref="Task.WhenAll"/>
/// harness for the chosen N. The metric to read is the
/// <c>Ampelum / EmptyAsync</c> ratio at each N, NOT the absolute Ampelum
/// scaling vs N=1 — the latter is dominated by harness dispatch cost at
/// sub-microsecond per-task workloads (32-core box: EmptyAsync at N=64 is
/// ~30× the EmptyAsync at N=1 with no Ampelum code present).
/// </para>
/// </summary>
[MemoryDiagnoser]
public class Scenario09_NParallelScaling
{
    /// <summary>N values — 1 (baseline), 8 (typical I/O fan-out),
    /// 32 (above typical core count), 64 (stress).</summary>
    [Params(1, 8, 32, 64)]
    public int Parallelism { get; set; }

    private YieldingMockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private QaSig _input = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new YieldingMockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        _ampelum = Predict.Create<QaSig>(_mock);
        _input = new QaSig { Question = "q" };
    }

    [Benchmark(Baseline = true)]
    public async Task Ampelum()
    {
        if (Parallelism == 1)
        {
            await _ampelum.ForwardAsync(_input);
            return;
        }

        var tasks = new Task<Prediction<QaSig>>[Parallelism];
        for (int i = 0; i < Parallelism; i++)
            tasks[i] = _ampelum.ForwardAsync(_input);
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Threadpool-dispatch + <see cref="Task.WhenAll"/> harness floor — each task
    /// does only <see cref="Task.Yield"/>, no Ampelum code. The ratio of
    /// <see cref="Ampelum"/> to this benchmark at a given N isolates Ampelum's
    /// per-task overhead from the harness's per-N fan-out cost.
    /// </summary>
    [Benchmark]
    public async Task EmptyAsync()
    {
        if (Parallelism == 1) { await Task.Yield(); return; }
        var tasks = new Task[Parallelism];
        for (int i = 0; i < Parallelism; i++) tasks[i] = DoYield();
        await Task.WhenAll(tasks);
        static async Task DoYield() => await Task.Yield();
    }
}
