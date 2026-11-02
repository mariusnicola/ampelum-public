using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using Ampelum.ComparativeBenchmarks.Competitors.Sk;
using SignalGrid.Ampelum.Hooks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S3 — Predict + 4-hook pipeline orchestration overhead.
///
/// Plan name: Scenario02_PredictPlus4HookPipeline (must-be-#1 per §B3).
/// Budget D2: end-to-end overhead with 4 hooks ≤ 10 µs above no-hooks.
///
/// alpha.4 wires four FAST noop <see cref="IHook"/> impls to characterise
/// the pipeline cost (HookContext + ordering + before/after dispatch). The
/// real budget+audit+retry+log mix lands in alpha.5; the wiring shape stays
/// identical so adding the production hooks does not change the table layout.
/// SK competitor uses an inline 4-stage delegate chain (see
/// <see cref="SkPipeline"/>) until alpha.5 wires the real Kernel filter
/// pipeline.
/// </summary>
[MemoryDiagnoser]
public class Scenario02_PredictPlus4HookPipeline
{
    private MockChatClient _mock = null!;
    private Predict<QaSig> _ampelum = null!;
    private Kernel _skKernel = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        var ctx = new AmpelumContext(
                _mock,
                new ChatAdapter(),
                NullTraceSink.Instance,
                NullLoggerFactory.Instance)
            .WithHook(new NoopHook(order: 1))
            .WithHook(new NoopHook(order: 2))
            .WithHook(new NoopHook(order: 3))
            .WithHook(new NoopHook(order: 4));
        _ampelum = new Predict<QaSig>(ctx);
        _skKernel = SkPipeline.BuildKernel(_mock);
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum()
        => await _ampelum.ForwardAsync(new QaSig { Question = "q" });

    [Benchmark]
    public async Task<string> Sk() => await SkPipeline.RunAsync(_skKernel, "q");

    /// <summary>Cheapest possible IHook — every method returns a default ValueTask.</summary>
    private sealed class NoopHook : IHook
    {
        public NoopHook(int order) => Order = order;
        public int Order { get; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }
}
