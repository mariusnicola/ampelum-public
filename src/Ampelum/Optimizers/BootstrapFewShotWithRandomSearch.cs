using System.Diagnostics;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// Wraps <see cref="BootstrapFewShot{TProgram}"/> in a random-seed search loop.
/// Runs <c>numCandidates</c> trials, each with a different shuffle of the trainset,
/// and returns the candidate whose mean trainset score (under the supplied metric)
/// is highest.
///
/// <para>
/// Scoring mode depends on whether <c>scoringRunner</c> is supplied to the constructor.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>With scoringRunner (correct mode):</b> each candidate is bound via
///     <see cref="OptimizedProgram{TProgram}.Bind"/> and run-and-scored against the
///     trainset via the supplied runner; the trial whose mean actual-vs-expected score is
///     highest wins.
///   </description></item>
///   <item><description>
///     <b>Without scoringRunner (degenerate mode, retained for backward compatibility):</b>
///     candidates are scored by <c>metric.Score(ex.Outputs, ex.Outputs, â€¦)</c>, which compares
///     the example's expected output to itself. All trials score identically and the first
///     wins by tie-break â€” the optimization is noise. Pass a scoringRunner for real
///     evaluation.
///   </description></item>
/// </list>
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class BootstrapFewShotWithRandomSearch<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly BootstrapFewShot<TProgram>.ProgramRunner _programRunner;
    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>>? _scoringRunner;
    readonly int _numCandidates;
    readonly int _maxBootstrappedDemos;
    readonly int? _seed;
    readonly double _metricThreshold;

    /// <summary>Number of random-search trials.</summary>
    public int NumCandidates => _numCandidates;

    /// <summary>Bootstrap demo cap per trial.</summary>
    public int MaxBootstrappedDemos => _maxBootstrappedDemos;

    /// <summary>Lowest metric score a run needs for its traces to become demos.</summary>
    public double MetricThreshold => _metricThreshold;

    /// <summary>Construct a random-search optimizer.</summary>
    /// <param name="programRunner">Trace-emitting runner used by the inner <c>BootstrapFewShot</c>.</param>
    /// <param name="numCandidates">Number of random-shuffle trials.</param>
    /// <param name="maxBootstrappedDemos">Per-trial demo cap.</param>
    /// <param name="seed">Optional deterministic shuffle seed.</param>
    /// <param name="scoringRunner">
    /// Optional run-and-score runner. When non-null, each candidate is bound and evaluated
    /// against the trainset via this runner; when null, scoring falls back to the degenerate
    /// self-comparison mode (see class XML-doc).
    /// </param>
    /// <param name="metricThreshold">Lowest metric score a run needs for its traces to become demos. Default 1.0, for a metric in [0, 1]; use a lower value for a metric on another scale.</param>
    public BootstrapFewShotWithRandomSearch(
        BootstrapFewShot<TProgram>.ProgramRunner programRunner,
        int numCandidates = 8,
        int maxBootstrappedDemos = 4,
        int? seed = null,
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>>? scoringRunner = null,
        double metricThreshold = 1.0)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentOutOfRangeException.ThrowIfLessThan(numCandidates, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBootstrappedDemos);
        _programRunner = programRunner;
        _numCandidates = numCandidates;
        _maxBootstrappedDemos = maxBootstrappedDemos;
        _seed = seed;
        _scoringRunner = scoringRunner;
        _metricThreshold = metricThreshold;
    }

    /// <inheritdoc />
    public async Task<OptimizedProgram<TProgram>> CompileAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        IProgress<OptimizerProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(trainset);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(BootstrapFewShotWithRandomSearch<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        OptimizedProgram<TProgram>? best = null;
        double bestScore = double.NegativeInfinity;

        for (int i = 0; i < _numCandidates; i++)
        {
            ct.ThrowIfCancellationRequested();

            int effectiveSeed = _seed.HasValue ? _seed.Value + i : Random.Shared.Next();
            var shuffled = Shuffle(trainset, effectiveSeed);

            var bootstrapper = new BootstrapFewShot<TProgram>(
                _programRunner, maxBootstrappedDemos: _maxBootstrappedDemos, metricThreshold: _metricThreshold);
            var candidate = await bootstrapper.CompileAsync(
                program, shuffled, metric, context, null, ct).ConfigureAwait(false);

            double score = await ScoreCandidateAsync(
                candidate, trainset, metric, context, ct).ConfigureAwait(false);

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }

            progress?.Report(new OptimizerProgress(
                Phase: $"trial {i + 1}/{_numCandidates}",
                Step: i + 1,
                TotalSteps: _numCandidates,
                BestScore: bestScore));
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFewShotWithRandomSearch<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFewShotWithRandomSearch<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(bestScore < 0 ? 0.0 : bestScore,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BootstrapFewShotWithRandomSearch<TProgram>)));

        activity?.SetTag("optimizer.final_score", bestScore);

        return best! with { Score = bestScore < 0 ? 0.0 : bestScore };
    }

    async Task<double> ScoreCandidateAsync(
        OptimizedProgram<TProgram> candidate,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        CancellationToken ct)
    {
        if (trainset.Count == 0) return 0.0;

        if (_scoringRunner is null)
        {
            // Degenerate mode preserved for v0.5.0 ctor compatibility (see class XML-doc).
            double sum = 0.0;
            foreach (var ex in trainset)
                sum += metric.Score(ex.Outputs, ex.Outputs, _placeholderMeta);
            return sum / trainset.Count;
        }

        // Real eval: bind the candidate so demos/overrides are wired into submodule contexts,
        // then run-and-score per example.
        var bound = candidate.Bind(context);
        double total = 0.0;
        int evaluated = 0;
        foreach (var ex in trainset)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var actual = await _scoringRunner(bound, ex, context, ct).ConfigureAwait(false);
                total += metric.Score(actual.Outputs, ex.Outputs, _placeholderMeta);
                evaluated++;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // skip examples that throw in metric or runner
            }
        }
        return evaluated > 0 ? total / evaluated : 0.0;
    }

    static IReadOnlyList<Example> Shuffle(IReadOnlyList<Example> list, int seed)
    {
        var copy = new List<Example>(list);
        var rng = new Random(seed);
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
