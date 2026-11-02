using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Orchestration;

/// <summary>
/// Iterative execution primitive: repeatedly evaluates a predicate module and, while the predicate
/// returns <c>ShouldContinue=true</c>, executes the body module with the current state. Stops when
/// the predicate returns <c>ShouldContinue=false</c>, or <c>maxIterations</c> is reached.
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
    /// <param name="maxIterations">Hard upper bound on iterations (default 100).</param>
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

        while (iteration < _maxIterations)
        {
            ct.ThrowIfCancellationRequested();
            iteration++;

            // Evaluate predicate
            var predResult = await _predicate.ForwardAsync(state, ct);
            childTraces.Add(predResult.Trace);

            if (!predResult.Value.ShouldContinue)
            {
                stopReason = predResult.Value.Reason ?? "predicate_break";
                break;
            }

            // Execute body
            var bodyResult = await _body.ForwardAsync(state, ct);
            childTraces.Add(bodyResult.Trace);
            state = bodyResult.Value;
        }

        var reason = iteration >= _maxIterations ? "max_iterations" : stopReason;

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