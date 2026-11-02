using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum;
using Ampelum.IntegrationTests.Fixtures;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Ampelum.IntegrationTests.Pareto;

/// <summary>
/// Pareto integration test #8 (master plan Â§6.H, alpha.2). Hooks pipeline ordering: a 4-hook
/// stack with non-monotonic <c>Order</c> values runs OnBefore in ascending order and OnAfter in
/// descending order. A short-circuit hook (assigns <c>ctx.Outputs</c> in OnBefore) aborts the
/// remaining OnBefore chain, skips the LM call, then still runs OnAfter in descending order.
/// </summary>
/// <remarks>
/// Behaviour reference: <c>HookPipeline.RunBeforeAsync</c> breaks out of the OnBefore loop the
/// moment <c>ctx.Outputs is not null</c> (PSQ-11 short-circuit). Predict's outer flow honours that
/// signal and bypasses formatting + the LM call (see <c>Predict.cs</c> Â§"PSQ-11 short-circuit").
/// </remarks>
public sealed class Pareto08_HookPipelineOrdering : IClassFixture<ParetoTestFixture>
{
    readonly ParetoTestFixture _fx;

    /// <summary>xUnit ctor injection.</summary>
    public Pareto08_HookPipelineOrdering(ParetoTestFixture fx) => _fx = fx;

    /// <summary>4-hook stack: ascending OnBefore, descending OnAfter, no short-circuit, all hooks fire twice.</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task FourHookStack_NoShortCircuit_AscendingThenDescending()
    {
        var ct = TestContext.Current.CancellationToken;
        var trace = new List<string>();

        var h0 = new TracingHook("alpha", order: 5, trace);
        var h1 = new TracingHook("beta", order: 50, trace);
        var h2 = new TracingHook("gamma", order: 100, trace);
        var h3 = new TracingHook("delta", order: 200, trace);

        using var fake = new FakeChatClient();
        fake.EnqueueText("[[ ## Answer ## ]]\nLisbon\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            // Intentionally inserted out of Order to prove the pipeline sorts by Order, not by index.
            { Hooks = new IHook[] { h2, h0, h3, h1 } };

        var predict = new Predict<QaSig>(ctx);
        var result = await predict.ForwardAsync(new QaSig { Question = "Capital of Portugal?" }, ct: ct);
        result.Value.Answer.ShouldBe("Lisbon");

        trace.ShouldBe(new[]
        {
            "before:alpha",  // Order=5
            "before:beta",   // Order=50
            "before:gamma",  // Order=100
            "before:delta",  // Order=200
            "after:delta",   // descending â€” last in OnBefore is first in OnAfter
            "after:gamma",
            "after:beta",
            "after:alpha",
        });
    }

    /// <summary>Short-circuit: middle hook seeds <c>ctx.Outputs</c>; later OnBefore hooks are skipped;
    /// LM is never called; OnAfter still runs descending across ALL hooks (verified against the source's
    /// fall-through into <c>RunAfterAsync</c> on the short-circuit path).</summary>
    [Fact]
    [Trait("Cost", "Cheap")]
    public async Task ShortCircuit_SecondHookPopulatesOutputs_SkipsLm_StillRunsAllOnAfterDescending()
    {
        var ct = TestContext.Current.CancellationToken;
        var trace = new List<string>();

        var h0 = new TracingHook("alpha", order: 5, trace);
        var h1 = new ShortCircuitHook("beta", order: 50, trace,
            outputs: new Dictionary<string, object?> { ["Answer"] = "from-cache" });
        var h2 = new TracingHook("gamma", order: 100, trace);
        var h3 = new TracingHook("delta", order: 200, trace);

        using var fake = new FakeChatClient();
        // Enqueue a response that MUST NOT be used â€” short-circuit short-cuts the LM.
        fake.EnqueueText("[[ ## Answer ## ]]\nshould-not-arrive\n[[ ## completed ## ]]");

        var ctx = new AmpelumContext(
            fake,
            new ChatAdapter(),
            NullTraceSink.Instance,
            NullLoggerFactory.Instance)
            { Hooks = new IHook[] { h0, h1, h2, h3 } };

        var predict = new Predict<QaSig>(ctx);
        var result = await predict.ForwardAsync(new QaSig { Question = "Capital of Portugal?" }, ct: ct);

        // Outputs came from the hook, not from the (unconsumed) fake response.
        result.Value.Answer.ShouldBe("from-cache");
        fake.RecordedCalls.Count.ShouldBe(0);

        trace.ShouldBe(new[]
        {
            "before:alpha",       // Order=5
            "before:beta(short)", // Order=50 â€” populates Outputs, breaks the loop
            "after:delta",        // descending OnAfter still hits all 4 hooks
            "after:gamma",
            "after:beta",
            "after:alpha",
        });
    }

    sealed class TracingHook : IHook
    {
        readonly string _name;
        readonly List<string> _trace;
        public TracingHook(string name, int order, List<string> trace)
        {
            _name = name; Order = order; _trace = trace;
        }
        public int Order { get; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            lock (_trace) _trace.Add($"before:{_name}");
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            lock (_trace) _trace.Add($"after:{_name}");
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }

    sealed class ShortCircuitHook : IHook
    {
        readonly string _name;
        readonly List<string> _trace;
        readonly IReadOnlyDictionary<string, object?> _outputs;
        public ShortCircuitHook(string name, int order, List<string> trace,
            IReadOnlyDictionary<string, object?> outputs)
        {
            _name = name; Order = order; _trace = trace; _outputs = outputs;
        }
        public int Order { get; }
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            lock (_trace) _trace.Add($"before:{_name}(short)");
            ctx.Outputs = _outputs;
            return default;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct)
        {
            lock (_trace) _trace.Add($"after:{_name}");
            return default;
        }
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => default;
    }
}
