using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Iterative execution primitive: repeatedly evaluates a predicate module and, while the predicate
/// returns <c>ShouldContinue=true</c>, executes the body module with the current state. Stops when
/// the predicate returns <c>ShouldContinue=false</c>, or the body has run <c>maxIterations</c> times and the
/// predicate still says continue (reason <c>max_iterations</c>).
/// </summary>
/// <typeparam name="TSig">The signature type shaped by the loop body.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class Loop<TSig> : IModule<TSig, LoopResult<TSig>>
    where TSig : ISignature
{
    readonly IModule<TSig, LoopPredicateResult<TSig>> _predicate;
    readonly IModule<TSig, TSig> _body;
    readonly int _maxIterations;

    /// <summary>
    /// Creates a loop that executes <paramref name="body"/> while <paramref name="predicate"/>
    /// returns <see cref="LoopPredicateResult{TSig}"/> with <c>ShouldContinue=true</c>.
    /// </summary>
    /// <param name="predicate">Module that decides whether the loop should continue.</param>
    /// <param name="body">Module that computes the next state from the current state.</param>
    /// <param name="maxIterations">Hard upper bound on body runs (default 100).</param>
    public Loop(
        IModule<TSig, LoopPredicateResult<TSig>> predicate,
        IModule<TSig, TSig> body,
        int maxIterations = 100)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(body);
        if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations), "Must be â‰¥ 1.");
        _predicate = predicate;
        _body = body;
        _maxIterations = maxIterations;
    }

    /// <inheritdoc/>
    public async Task<Prediction<LoopResult<TSig>>> ForwardAsync(TSig inputs, CancellationToken ct = default)
    {
        var state = inputs;
        var iteration = 0;
        var childTraces = new List<TraceRecord>();
        var stopReason = "max_iterations";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // The predicate is asked before every body run and once after the last allowed one, so a stop
            // on that last check keeps its own reason; only a predicate that still says continue is cut off.
            var predResult = await _predicate.ForwardAsync(state, ct);
            childTraces.Add(predResult.Trace);

            if (!predResult.Value.ShouldContinue)
            {
                stopReason = predResult.Value.Reason ?? "predicate_break";
                break;
            }

            if (iteration >= _maxIterations)
            {
                break;
            }

            var bodyResult = await _body.ForwardAsync(state, ct);
            childTraces.Add(bodyResult.Trace);
            state = bodyResult.Value;
            iteration++;
        }

        var reason = stopReason;

        var output = new LoopResult<TSig>(state, iteration, reason);

        // Build composition trace
        var inputsDict = new Dictionary<string, object?>
            { ["iteration"] = iteration, ["reason"] = reason };
        var outputsDict = new Dictionary<string, object?>
            { ["final_state"] = state!, ["iterations"] = iteration, ["reason"] = reason };
        var childIds = childTraces.Select(t => t.Id).ToArray();
        var extraMeta = new Dictionary<string, object?>
        {
            [Composition.TraceKeys.CompositionPrimitive] = "Loop",
            [Composition.TraceKeys.LoopIterations] = iteration,
            [Composition.TraceKeys.LoopConverged] = reason != "max_iterations",
            [Composition.TraceKeys.LoopChildTraceIds] = (IReadOnlyList<Guid>)childIds,
        };
        var trace = Composition.CompositionTrace.Aggregate(
            id: Guid.NewGuid(),
            moduleName: $"Loop<{typeof(TSig).Name}>",
            signatureType: typeof(LoopResult<TSig>),
            inputs: inputsDict,
            outputs: outputsDict,
            childTraces: childTraces,
            extraMetadata: extraMeta);

        return new Prediction<LoopResult<TSig>>(output, trace);
    }
}