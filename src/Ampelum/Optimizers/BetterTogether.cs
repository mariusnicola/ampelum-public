using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>
/// BetterTogether optimizer (as in <c>dspy.BetterTogether</c>): runs a prompt optimizer and a weight optimizer
/// in turn. The strategy is a list of <c>p</c> (prompt step) and <c>w</c> (weight step) joined by <c> -&gt; </c>,
/// default <c>p -&gt; w -&gt; p</c>; anything else is rejected with <see cref="ArgumentException"/>.
/// <list type="number">
///   <item><description>
///     Each step compiles the result of the previous step bound to the context (the program itself for the first
///     step). What a step produced is merged into what the steps before it produced: per submodule, the demos,
///     the instruction override and the chat client of a later step replace those of an earlier one.
///   </description></item>
///   <item><description>
///     After each step the merged result is bound and run on the validation set (the <c>valset</c> argument, else a
///     seeded holdout of 20 percent of the trainset, which the steps do not see). The mean metric score is the score
///     of that step.
///   </description></item>
///   <item><description>
///     The result is the merged result of the step with the best score, the earliest on a tie. Its
///     <c>Score</c> is that score and its <c>StepScores</c> are the scores of all steps in order. Every progress
///     report carries the scores so far in the <c>StepScores</c> extra.
///   </description></item>
/// </list>
/// A step receives the program bound with everything the steps before it produced. A step that binds candidates again
/// (as <see cref="BootstrapFewShotWithRandomSearch{TProgram}"/> does) keeps the tuned client, demos and instruction
/// overrides of the earlier steps, because <see cref="OptimizedProgram{TProgram}.Bind"/> keeps whatever an earlier
/// Bind set and a candidate does not. This works for <c>Predict</c> and <c>ChainOfThought</c> submodules only; any
/// other submodule is bound to the context again and loses the earlier steps' work. A runner or metric exception on one example scores 0 and
/// is counted (the <c>RunnerErrors</c> extra and activity tag <c>optimizer.runner_errors</c>). Only cancellation of the
/// token passed to <c>CompileAsync</c> propagates.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class BetterTogether<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    const int HoldoutPercent = 20;
    const int SplitSalt = 80;

    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> _programRunner;
    readonly IOptimizer<TProgram> _promptOptimizer;
    readonly IOptimizer<TProgram>? _weightOptimizer;
    readonly IReadOnlyList<char> _steps;
    readonly IReadOnlyList<Example>? _valset;
    readonly int _seed;

    /// <summary>Construct a BetterTogether optimizer.</summary>
    /// <param name="programRunner">
    /// Runs the program against an example, returning the produced outputs. It scores every step, and it
    /// feeds the default optimizers, so it must write the trace of each module call to the trace sink of the
    /// context it receives.
    /// </param>
    /// <param name="promptOptimizer">Prompt optimizer. Default: <see cref="BootstrapFewShotWithRandomSearch{TProgram}"/>.</param>
    /// <param name="weightOptimizer">Weight optimizer. Default: <see cref="BootstrapFinetune{TProgram}"/> on <paramref name="fineTuneProvider"/>.</param>
    /// <param name="fineTuneProvider">Needed when the strategy has a weight step and <paramref name="weightOptimizer"/> is null.</param>
    /// <param name="strategy">Steps joined by <c> -&gt; </c>. Default <c>p -&gt; w -&gt; p</c>.</param>
    /// <param name="valset">Validation set. Default: a seeded holdout of the trainset passed to <c>CompileAsync</c>.</param>
    /// <param name="seed">Seed of the holdout split and of the default prompt optimizer. Default 0.</param>
    public BetterTogether(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> programRunner,
        IOptimizer<TProgram>? promptOptimizer = null,
        IOptimizer<TProgram>? weightOptimizer = null,
        IFineTuneProvider? fineTuneProvider = null,
        string strategy = "p -> w -> p",
        IReadOnlyList<Example>? valset = null,
        int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        if (valset is { Count: 0 })
            throw new ArgumentException("Valset must contain at least one example.", nameof(valset));
        _steps = ParseStrategy(strategy);
        _programRunner = programRunner;
        _valset = valset;
        _seed = seed;

        async Task Trace(TProgram program, Example example, AmpelumContext context, CancellationToken ct) =>
            _ = await programRunner(program, example, context, ct).ConfigureAwait(false);

        _promptOptimizer = promptOptimizer ?? new BootstrapFewShotWithRandomSearch<TProgram>(
            Trace, seed: seed, scoringRunner: programRunner);
        if (weightOptimizer is null && _steps.Contains('w'))
        {
            if (fineTuneProvider is null)
                throw new ArgumentException(
                    "A weight step needs a weight optimizer or a fine-tune provider.", nameof(fineTuneProvider));
            weightOptimizer = new BootstrapFinetune<TProgram>(Trace, fineTuneProvider);
        }
        _weightOptimizer = weightOptimizer;
    }

    /// <summary>The steps in order: <c>p</c> for a prompt step, <c>w</c> for a weight step.</summary>
    public IReadOnlyList<char> Steps => _steps;

    /// <summary>Splits a strategy string into its steps.</summary>
    internal static IReadOnlyList<char> ParseStrategy(string strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var steps = new List<char>();
        foreach (var token in strategy.Split(" -> "))
        {
            if (token is not ("p" or "w"))
                throw new ArgumentException(
                    $"Invalid strategy '{strategy}': steps are 'p' or 'w' joined by ' -> '.", nameof(strategy));
            steps.Add(token[0]);
        }
        return steps;
    }

    /// <inheritdoc/>
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
        if (trainset.Count == 0)
            throw new ArgumentException("Trainset must contain at least one example.", nameof(trainset));

        ct.ThrowIfCancellationRequested();

        using var activity = Diagnostics.Source.StartActivity("SignalGrid.Ampelum.optimizer.compile");
        activity?.SetTag("optimizer.type", nameof(BetterTogether<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();
        var (train, valid) = Split(trainset);

        var merged = new OptimizedProgram<TProgram>(
            program,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>())
        {
            TrainsetSize = trainset.Count,
        };
        var current = program;
        var scores = new List<double>();
        OptimizedProgram<TProgram>? best = null;
        double bestScore = double.NegativeInfinity;
        int errors = 0;

        for (int i = 0; i < _steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var optimizer = _steps[i] == 'p' ? _promptOptimizer : _weightOptimizer!;
            var compiled = await optimizer.CompileAsync(current, train, metric, context, null, ct).ConfigureAwait(false);
            merged = Merge(merged, compiled);
            current = merged.Bind(context);

            var (score, stepErrors) = await ScoreAsync(current, valid, metric, context, ct).ConfigureAwait(false);
            errors += stepErrors;
            scores.Add(score);
            if (score > bestScore)
            {
                best = merged;
                bestScore = score;
            }

            progress?.Report(new OptimizerProgress(
                Phase: _steps[i] == 'p' ? "prompt" : "weight",
                Step: i + 1,
                TotalSteps: _steps.Count,
                BestScore: bestScore,
                Extras: new Dictionary<string, object?>
                {
                    ["StepScores"] = scores.ToArray(),
                    ["RunnerErrors"] = errors,
                }));
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BetterTogether<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BetterTogether<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(bestScore,
            new KeyValuePair<string, object?>("optimizer.type", nameof(BetterTogether<TProgram>)));

        activity?.SetTag("optimizer.final_score", bestScore);
        activity?.SetTag("optimizer.runner_errors", errors);

        return best! with { Score = bestScore, StepScores = scores };
    }

    (IReadOnlyList<Example> Train, IReadOnlyList<Example> Valid) Split(IReadOnlyList<Example> trainset)
    {
        if (_valset is not null) return (trainset, _valset);
        // One example cannot be split: it trains and validates.
        if (trainset.Count == 1) return (trainset, trainset);

        var shuffled = new List<Example>(trainset);
        var rng = new Random(unchecked(_seed * 7919 + SplitSalt));
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        int validCount = Math.Clamp(trainset.Count * HoldoutPercent / 100, 1, trainset.Count - 1);
        return (shuffled.Skip(validCount).ToList(), shuffled.Take(validCount).ToList());
    }

    /// <summary>What a later step produced replaces, per submodule, what an earlier one produced.</summary>
    static OptimizedProgram<TProgram> Merge(OptimizedProgram<TProgram> earlier, OptimizedProgram<TProgram> later)
    {
        var demos = new Dictionary<string, IReadOnlyList<Example>>(earlier.DemosBySubmodule);
        foreach (var (name, list) in later.DemosBySubmodule) demos[name] = list;
        var instructions = new Dictionary<string, string>(earlier.InstructionOverrides);
        foreach (var (name, text) in later.InstructionOverrides) instructions[name] = text;
        var clients = new Dictionary<string, Microsoft.Extensions.AI.IChatClient>(earlier.ChatClients);
        foreach (var (name, client) in later.ChatClients) clients[name] = client;
        return earlier with
        {
            DemosBySubmodule = demos,
            InstructionOverrides = instructions,
            ChatClients = clients,
        };
    }

    async Task<(double Mean, int Errors)> ScoreAsync(
        TProgram bound, IReadOnlyList<Example> examples, IMetric metric, AmpelumContext context, CancellationToken ct)
    {
        int errors = 0;
        double sum = 0.0;
        foreach (var example in examples)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var actual = await _programRunner(bound, example, context, ct).ConfigureAwait(false);
                double score = metric.Score(actual.Outputs, example.Outputs, _placeholderMeta);
                sum += double.IsNaN(score) ? 0.0 : score;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A timeout cancels the call without cancelling ct; it scores 0 like any other failure.
                errors++;
            }
        }
        return (sum / examples.Count, errors);
    }
}
