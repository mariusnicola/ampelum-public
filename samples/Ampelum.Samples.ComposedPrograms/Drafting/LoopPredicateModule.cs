using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>Body predictions of one loop run, shared by the body and the predicate.</summary>
internal sealed class LoopHistory<TSig> where TSig : ISignature
{
    readonly List<Prediction<TSig>> _runs = [];

    public int BodyRuns => _runs.Count;

    public void Add(Prediction<TSig> run) => _runs.Add(run);

    /// <summary>Every body run except the latest, which is the state under test.</summary>
    public IReadOnlyList<Prediction<TSig>> Earlier => _runs.Take(Math.Max(0, _runs.Count - 1)).ToList();
}

/// <summary>
/// Bridges a <see cref="LoopPredicate{T}"/> to the module shape that <see cref="Loop{TSig}"/> expects.
/// A predicate says when to stop and the loop asks whether to continue, so the answer is inverted.
/// </summary>
internal sealed class LoopPredicateModule<TSig>(LoopPredicate<TSig> stopWhen, LoopHistory<TSig> history)
    : IModule<TSig, LoopPredicateResult<TSig>> where TSig : ISignature
{
    public Task<Prediction<LoopPredicateResult<TSig>>> ForwardAsync(TSig state, CancellationToken ct = default)
    {
        var loopState = new LoopState<TSig>
        {
            Iteration = history.BodyRuns,
            CurrentValue = state,
            History = history.Earlier,
        };
        var stop = stopWhen.ShouldStop(loopState);
        var verdict = new LoopPredicateResult<TSig>(!stop, stop ? $"stopped after {history.BodyRuns} body runs" : null);
        var trace = new TraceRecord(Guid.NewGuid(), "LoopPredicate", typeof(TSig),
            new Dictionary<string, object?>(), new Dictionary<string, object?> { ["stop"] = stop },
            [], "", null, TimeSpan.Zero, null, new Dictionary<string, object?>());
        return Task.FromResult(new Prediction<LoopPredicateResult<TSig>>(verdict, trace));
    }
}
