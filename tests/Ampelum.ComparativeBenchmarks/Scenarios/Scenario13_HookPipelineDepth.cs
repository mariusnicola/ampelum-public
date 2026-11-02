using SignalGrid.Ampelum;
using Ampelum.ComparativeBenchmarks.Competitors.Shared;
using SignalGrid.Ampelum.Hooks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ampelum.ComparativeBenchmarks.Scenarios;

/// <summary>
/// User-brief S13 — hook-pipeline depth scaling.
///
/// Plan name: Scenario13_HookPipelineDepth.
/// Target: sub-linear per-hook overhead growth, and verifies that
/// Order-ascending+descending invocation does NOT add allocation cost
/// per hook beyond the dispatch.
///
/// <para>Setup wires N <see cref="NoopHook"/> instances at unique <c>Order</c> values
/// (positive and negative interleaved so the pipeline must sort them) and runs
/// <see cref="Predict{TSig}.ForwardAsync"/> through them. The expectation is that
/// per-hook cost stays below the per-forward fixed cost so the slope flattens
/// on the ranker chart.</para>
///
/// <para>No competitor row: SK kernel filters and AutoGen.NET reply pipelines are
/// not API-comparable to the typed-IHook contract; the scenario is a self-scaling
/// must-be-acceptable rather than head-to-head. The ranker emits "—" for those.</para>
/// </summary>
[MemoryDiagnoser]
public class Scenario13_HookPipelineDepth
{
    /// <summary>Hook depth — 1, 4, 8, 16. The 4-row case overlaps S3 numerically; kept
    /// here so a single ranker query can produce the depth-scaling chart.</summary>
    [Params(1, 4, 8, 16)]
    public int Hooks { get; set; }

    MockChatClient _mock = null!;
    Predict<QaSig> _ampelum = null!;
    QaSig _input = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mock = new MockChatClient("[[ ## Answer ## ]]\n42\n[[ ## completed ## ]]");
        var ctx = new AmpelumContext(
            _mock,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        // Interleave positive/negative orders so the pipeline must sort, exercising
        // the Order-ascending(before) + descending(after) symmetric dispatch.
        for (var i = 0; i < Hooks; i++)
        {
            var order = (i % 2 == 0) ? i + 1 : -(i + 1);
            ctx = ctx.WithHook(new NoopHook(order));
        }
        _ampelum = new Predict<QaSig>(ctx);
        _input = new QaSig { Question = "q" };
    }

    [Benchmark(Baseline = true)]
    public async Task<Prediction<QaSig>> Ampelum() => await _ampelum.ForwardAsync(_input);

    /// <summary>Cheapest possible IHook — every method returns a default ValueTask.</summary>
    sealed class NoopHook : IHook
    {
        public NoopHook(int order) => Order = order;
        public int Order { get; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => default;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }
}
