using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;

namespace Ampelum.Samples.ComposedPrograms.Drafting;

/// <summary>
/// Shortens a note until a stop rule says it is good enough. The rule is built from registered
/// predicates, so the loop code does not change when the rule does.
/// </summary>
public sealed class TightenStage
{
    public const string FitsRule = "fits";
    public const string StalledRule = "stalled";
    public const string LimitRule = "limit";

    readonly AmpelumContext _ctx;
    readonly ReleaseNotesOptions _options;

    static readonly object Gate = new();
    static bool _registered;

    /// <summary>
    /// Registers the three named predicates, keyed by signature type and rule name. Every entry point
    /// calls it first, so no caller depends on which class was touched before, and it can run on any thread.
    /// </summary>
    public static void RegisterPredicates()
    {
        lock (Gate)
        {
            if (_registered)
                return;
            LoopPredicateRegistry.Register<TightenSig>(FitsRule, p => new FieldThreshold<TightenSig>(
                (string)p["field"]!, (decimal)p["threshold"]!, ThresholdDirection.LessThanOrEqual));
            LoopPredicateRegistry.Register<TightenSig>(StalledRule, p => new FieldChangeBelow<TightenSig>(
                (string)p["field"]!, (decimal)p["epsilon"]!));
            LoopPredicateRegistry.Register<TightenSig>(LimitRule, p => new MaxIterations<TightenSig>((int)p["max"]!));
            _registered = true;
        }
    }

    public TightenStage(AmpelumContext ctx, ReleaseNotesOptions options)
    {
        RegisterPredicates();
        _ctx = ctx;
        _options = options;
    }

    /// <summary>Stop when the note fits, or when it stopped shrinking and is close enough, or on the pass limit.</summary>
    public LoopPredicate<TightenSig> StopRule()
    {
        RegisterPredicates();
        var fits = LoopPredicateRegistry.Create<TightenSig>(FitsRule, new Dictionary<string, object?>
        {
            ["field"] = nameof(TightenSig.Words),
            ["threshold"] = (decimal)_options.MaxWords,
        });
        var closeEnough = LoopPredicateRegistry.Create<TightenSig>(FitsRule, new Dictionary<string, object?>
        {
            ["field"] = nameof(TightenSig.Words),
            ["threshold"] = (decimal)(_options.MaxWords * _options.ToleranceFactor),
        });
        var stalled = LoopPredicateRegistry.Create<TightenSig>(StalledRule, new Dictionary<string, object?>
        {
            ["field"] = nameof(TightenSig.Words),
            ["epsilon"] = _options.StalledEpsilon,
        });
        var limit = LoopPredicateRegistry.Create<TightenSig>(LimitRule, new Dictionary<string, object?>
        {
            ["max"] = _options.TightenPasses,
        });
        return new CompositeAny<TightenSig>(fits, new CompositeAll<TightenSig>(stalled, closeEnough), limit);
    }

    public async Task<(LoopResult<TightenSig> Result, int BodyRuns)> RunAsync(
        TightenSig start, LoopPredicate<TightenSig>? stopWhen = null, CancellationToken ct = default)
    {
        var history = new LoopHistory<TightenSig>();
        var predicate = new LoopPredicateModule<TightenSig>(stopWhen ?? StopRule(), history);
        var body = Step.OfPrediction<TightenSig, TightenSig>(async (state, token) =>
        {
            var run = await new Predict<TightenSig>(_ctx).ForwardAsync(state, ct: token).ConfigureAwait(false);
            // The shortened note becomes the draft of the next pass.
            var next = run with { Value = run.Value with { Draft = run.Value.Note } };
            history.Add(next);
            return next;
        });
        // Loop counts every predicate check as an iteration and reports a stop on the last allowed check
        // as max_iterations. Two above the pass limit keeps the stop rule from being mislabeled.
        var loop = new Loop<TightenSig>(predicate, body, _options.TightenPasses + 2);
        var prediction = await loop.ForwardAsync(start, ct).ConfigureAwait(false);
        return (prediction.Value, history.BodyRuns);
    }
}
