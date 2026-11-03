using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>Settings of <see cref="SimbaOptimizer{TProgram}"/>.</summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record SimbaOptions
{
    /// <summary>Trainset examples in the minibatch of each step. Default 8.</summary>
    public int MinibatchSize { get; init; } = 8;

    /// <summary>
    /// Programs drawn from the pool and run on the minibatch in each step, and the most new candidates a step
    /// builds. Default 4.
    /// </summary>
    public int CandidatesPerStep { get; init; } = 4;

    /// <summary>Number of steps. Default 6.</summary>
    public int MaxSteps { get; init; } = 6;

    /// <summary>Most demos a candidate carries for one submodule; the oldest are dropped. Default 4.</summary>
    public int MaxDemos { get; init; } = 4;

    /// <summary>Softmax temperature of the pool draw; lower favours the best candidates. Default 0.2.</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>Seed for the minibatches, the pool draws and the choice of strategy. Default 0.</summary>
    public int Seed { get; init; }
}

/// <summary>
/// SIMBA prompt optimizer (as in <c>dspy.SIMBA</c>). It grows a pool of candidates, each a set of demos and
/// instruction overrides, from the examples the program is unstable on.
/// <list type="number">
///   <item><description>
///     Step: a seeded minibatch of the trainset. <see cref="SimbaOptions.CandidatesPerStep"/> programs are drawn
///     from the pool, with replacement, with probability softmax(score / <see cref="SimbaOptions.Temperature"/>) where a
///     candidate's score is the mean of its minibatch scores so far (0 before it has one). Each runs on every
///     example of the minibatch.
///   </description></item>
///   <item><description>
///     Spread: per example, the highest minus the lowest score across the drawn runs. The examples with the
///     largest spread (at most <see cref="SimbaOptions.CandidatesPerStep"/>, none with a spread of 0) each yield one new
///     candidate, built on the program of that example's best run and on a submodule picked round robin.
///     The seeded RNG picks the strategy: append a demo (the example's inputs and the best run's outputs, the oldest
///     demo dropped beyond <see cref="SimbaOptions.MaxDemos"/>), or append a rule. For a rule the reflection model,
///     called through <see cref="HookedLmCall"/> with module name <c>SIMBA.Rule</c>, compares the best and the worst
///     run of that example and writes one rule, which is added on a new line to the submodule's instruction.
///   </description></item>
///   <item><description>
///     New candidates run on the same minibatch and join the pool.
///   </description></item>
///   <item><description>
///     After the last step the three candidates with the best minibatch score run on the whole validation set
///     (the <c>valset</c> argument, else the trainset). The one with the best mean is returned, the earliest on a
///     tie; its mean is the <c>Score</c>.
///   </description></item>
/// </list>
/// A demo holds the fields of the whole program (inputs of the example, outputs of the run), so it fits a program
/// whose only submodule has those fields; on a program with several submodules it is applied to the submodule
/// whose turn it is, and a candidate whose demo does not fit scores badly and drops out of the ranking.
/// A runner or metric exception on one example scores 0 for that example and is counted (activity tag
/// <c>optimizer.runner_errors</c> and the <c>RunnerErrors</c> extra of every progress report); a NaN score counts
/// as 0. Only cancellation of the token passed to <c>CompileAsync</c> propagates. A failing reflection call throws
/// <see cref="OptimizerException"/>. The same seed with a deterministic model gives the same result.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class SimbaOptimizer<TProgram> : IOptimizer<TProgram>
{
    const string SingleName = "__single__";
    const int FinalistCount = 3;
    const int MinibatchSalt = 1_000_000;
    const int PickSalt = 60;
    const int StrategySalt = 70;

    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> _programRunner;
    readonly IChatClient _reflectionClient;
    readonly SimbaOptions _options;
    readonly IReadOnlyList<Example>? _valset;

    /// <summary>Construct a SIMBA optimizer.</summary>
    /// <param name="programRunner">Runs the program against an example, returning the produced outputs.</param>
    /// <param name="reflectionClient">Chat client that writes a rule from the best and the worst run of an example.</param>
    /// <param name="options">Minibatch, step, demo, temperature and seed settings. Default: <see cref="SimbaOptions"/> defaults.</param>
    /// <param name="valset">Validation set for the final selection. Default: the trainset passed to <c>CompileAsync</c>.</param>
    public SimbaOptimizer(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> programRunner,
        IChatClient reflectionClient,
        SimbaOptions? options = null,
        IReadOnlyList<Example>? valset = null)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentNullException.ThrowIfNull(reflectionClient);
        options ??= new SimbaOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinibatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CandidatesPerStep, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDemos, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Temperature, 0.0);
        if (valset is { Count: 0 })
            throw new ArgumentException("Valset must contain at least one example.", nameof(valset));
        _programRunner = programRunner;
        _reflectionClient = reflectionClient;
        _options = options;
        _valset = valset;
    }

    /// <summary>The settings this optimizer runs with.</summary>
    public SimbaOptions Options => _options;

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
        activity?.SetTag("optimizer.type", nameof(SimbaOptimizer<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var names = program is IComposedModule { Submodules.Count: > 0 } composed
            ? composed.Submodules.Keys.ToList()
            : new List<string> { SingleName };

        var run = new Run(this, program, metric, context, names, trainset, _valset ?? trainset, progress);
        var (best, score) = await run.SearchAsync(ct).ConfigureAwait(false);

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(SimbaOptimizer<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(SimbaOptimizer<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(score,
            new KeyValuePair<string, object?>("optimizer.type", nameof(SimbaOptimizer<TProgram>)));

        activity?.SetTag("optimizer.final_score", score);
        activity?.SetTag("optimizer.runner_errors", run.Errors);

        return new OptimizedProgram<TProgram>(program, best.Demos, best.Instructions)
        {
            Score = score,
            TrainsetSize = trainset.Count,
        };
    }

    // ---- steps (internal so tests can drive them with hand-built tables) -----

    /// <summary>
    /// Softmax weights of candidate scores: exp((score - highest) / temperature). Unnormalized; the highest
    /// score has weight 1.
    /// </summary>
    internal static double[] SoftmaxWeights(IReadOnlyList<double> scores, double temperature)
    {
        double max = scores.Max();
        return scores.Select(s => Math.Exp((s - max) / temperature)).ToArray();
    }

    /// <summary>Draws an index with probability proportional to its weight.</summary>
    internal static int PickIndex(double[] weights, Random rng)
    {
        double pick = rng.NextDouble() * weights.Sum();
        int index = 0;
        for (; index < weights.Length - 1; index++)
        {
            pick -= weights[index];
            if (pick < 0) break;
        }
        return index;
    }

    /// <summary>
    /// Example indexes ordered by spread, the largest first (the lower index first on a tie). An example whose
    /// spread is 0 is left out. <paramref name="scores"/> is indexed by run, then by example.
    /// </summary>
    internal static List<int> RankBySpread(IReadOnlyList<IReadOnlyList<double>> scores) =>
        Enumerable.Range(0, scores[0].Count)
            .Select(j => (Index: j, Spread: scores.Max(run => run[j]) - scores.Min(run => run[j])))
            .Where(x => x.Spread > 0)
            .OrderByDescending(x => x.Spread)
            .ThenBy(x => x.Index)
            .Select(x => x.Index)
            .ToList();

    /// <summary>A copy of <paramref name="demos"/> with <paramref name="demo"/> added last for the submodule; the oldest go beyond <paramref name="maxDemos"/>.</summary>
    internal static Dictionary<string, IReadOnlyList<Example>> AppendDemo(
        IReadOnlyDictionary<string, IReadOnlyList<Example>> demos, string submodule, Example demo, int maxDemos)
    {
        var list = new List<Example>(demos.GetValueOrDefault(submodule) ?? []) { demo };
        return new Dictionary<string, IReadOnlyList<Example>>(demos) { [submodule] = list.TakeLast(maxDemos).ToList() };
    }

    /// <summary>A copy of <paramref name="instructions"/> where the submodule's instruction is <paramref name="current"/> and the rule on a new line.</summary>
    internal static Dictionary<string, string> AppendRule(
        IReadOnlyDictionary<string, string> instructions, string submodule, string current, string rule) =>
        new(instructions) { [submodule] = current + "\n" + rule };

    internal static string BuildRulePrompt(
        string submodule, string instruction, Example example, RunRecord best, RunRecord worst)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Module: ").Append(submodule).Append('\n');
        sb.Append("Current instruction:\n").Append(instruction).Append("\n\n");
        sb.Append("Inputs: ").Append(RenderFields(example.Inputs)).Append("\n\n");
        sb.Append("Best run, score ").Append(best.Score.ToString("0.###", CultureInfo.InvariantCulture)).Append(":\n");
        sb.Append("Outputs: ").Append(Describe(best)).Append("\n\n");
        sb.Append("Worst run, score ").Append(worst.Score.ToString("0.###", CultureInfo.InvariantCulture)).Append(":\n");
        sb.Append("Outputs: ").Append(Describe(worst)).Append("\n\n");
        sb.Append("Compare the two runs and write one short rule that makes the program behave like the best run.");
        return sb.ToString();
    }

    static string Describe(RunRecord run) =>
        run.Actual is null ? $"(the run failed: {run.Error})" : RenderFields(run.Actual);

    static string RenderFields(IReadOnlyDictionary<string, object?> fields) =>
        string.Join(", ", fields.Select(kv => $"{kv.Key}={Convert.ToString(kv.Value, CultureInfo.InvariantCulture)}"));

    static int Derive(int seed, int salt) => unchecked(seed * 7919 + salt);

    static List<Example> Shuffle(IReadOnlyList<Example> list, int seed)
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

    /// <summary>What running one example gave: the outputs (null when the run failed), the score, the failure.</summary>
    internal sealed record RunRecord(IReadOnlyDictionary<string, object?>? Actual, double Score, string? Error);

    /// <summary>One member of the pool.</summary>
    internal sealed class Candidate(
        IReadOnlyDictionary<string, IReadOnlyList<Example>> demos, IReadOnlyDictionary<string, string> instructions)
    {
        public IReadOnlyDictionary<string, IReadOnlyList<Example>> Demos { get; } = demos;
        public IReadOnlyDictionary<string, string> Instructions { get; } = instructions;
        /// <summary>Mean score of each minibatch run of this candidate.</summary>
        public List<double> History { get; } = [];
        public double Score => History.Count == 0 ? 0.0 : History.Average();
    }

    sealed class Run(
        SimbaOptimizer<TProgram> owner,
        TProgram program,
        IMetric metric,
        AmpelumContext context,
        List<string> names,
        IReadOnlyList<Example> train,
        IReadOnlyList<Example> valid,
        IProgress<OptimizerProgress>? progress)
    {
        readonly SimbaOptions _options = owner._options;
        readonly List<Candidate> _pool = [];
        int _attempts;

        public int Errors { get; private set; }

        public async Task<(Candidate Best, double Score)> SearchAsync(CancellationToken ct)
        {
            _pool.Add(new Candidate(new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>()));
            var pickRng = new Random(Derive(_options.Seed, PickSalt));
            var strategyRng = new Random(Derive(_options.Seed, StrategySalt));

            for (int step = 1; step <= _options.MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();

                var minibatch = Shuffle(train, Derive(_options.Seed, MinibatchSalt + step))
                    .Take(_options.MinibatchSize).ToList();
                var weights = SoftmaxWeights(_pool.Select(c => c.Score).ToList(), _options.Temperature);
                var drawn = Enumerable.Range(0, _options.CandidatesPerStep)
                    .Select(_ => _pool[PickIndex(weights, pickRng)]).ToList();

                var runs = new List<List<RunRecord>>();
                foreach (var candidate in drawn)
                {
                    var records = await EvaluateAsync(candidate, minibatch, ct).ConfigureAwait(false);
                    candidate.History.Add(records.Average(r => r.Score));
                    runs.Add(records);
                }

                var ranked = RankBySpread(runs.Select(r => (IReadOnlyList<double>)r.Select(x => x.Score).ToList()).ToList());
                var children = new List<Candidate>();
                foreach (int index in ranked.Take(_options.CandidatesPerStep))
                {
                    ct.ThrowIfCancellationRequested();

                    int bestRun = IndexOfExtreme(runs, index, highest: true);
                    int worstRun = IndexOfExtreme(runs, index, highest: false);
                    var parent = drawn[bestRun];
                    var submodule = names[_attempts++ % names.Count];
                    var child = strategyRng.Next(2) == 0
                        ? WithDemo(parent, submodule, minibatch[index], runs[bestRun][index])
                        : await WithRuleAsync(
                            parent, submodule, minibatch[index], runs[bestRun][index], runs[worstRun][index], ct)
                            .ConfigureAwait(false);
                    if (child is not null) children.Add(child);
                }

                foreach (var child in children)
                {
                    var records = await EvaluateAsync(child, minibatch, ct).ConfigureAwait(false);
                    child.History.Add(records.Average(r => r.Score));
                    _pool.Add(child);
                }

                Report("step", step, _pool.Max(c => c.Score));
            }

            Candidate? best = null;
            double bestScore = double.NegativeInfinity;
            var finalists = _pool.Select((c, i) => (Candidate: c, Index: i))
                .OrderByDescending(x => x.Candidate.Score)
                .ThenBy(x => x.Index)
                .Take(FinalistCount)
                .Select(x => x.Candidate);
            foreach (var finalist in finalists)
            {
                var records = await EvaluateAsync(finalist, valid, ct).ConfigureAwait(false);
                double mean = records.Average(r => r.Score);
                if (mean > bestScore)
                {
                    best = finalist;
                    bestScore = mean;
                }
            }

            Report("evaluate", _options.MaxSteps, bestScore);
            return (best!, bestScore);
        }

        // The first run on a tie.
        static int IndexOfExtreme(List<List<RunRecord>> runs, int example, bool highest)
        {
            int found = 0;
            for (int r = 1; r < runs.Count; r++)
            {
                double candidate = runs[r][example].Score;
                double current = runs[found][example].Score;
                if (highest ? candidate > current : candidate < current) found = r;
            }
            return found;
        }

        Candidate? WithDemo(Candidate parent, string submodule, Example example, RunRecord best)
        {
            // A run that failed has no outputs to show.
            if (best.Actual is null) return null;

            var demos = AppendDemo(
                parent.Demos, submodule, new Example(example.Inputs, best.Actual), _options.MaxDemos);
            return new Candidate(demos, parent.Instructions);
        }

        async Task<Candidate?> WithRuleAsync(
            Candidate parent, string submodule, Example example, RunRecord best, RunRecord worst, CancellationToken ct)
        {
            var current = OptimizerInstructions.Current(program!, submodule, parent.Instructions);
            var rule = await ReflectAsync(submodule, current, example, best, worst, ct).ConfigureAwait(false);
            if (rule.Length == 0) return null;

            return new Candidate(parent.Demos, AppendRule(parent.Instructions, submodule, current, rule));
        }

        async Task<string> ReflectAsync(
            string submodule, string current, Example example, RunRecord best, RunRecord worst, CancellationToken ct)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a prompt engineer. You write one rule for a language model program by comparing " +
                    "its best and its worst run on one example. Reply with the rule text only."),
                new(ChatRole.User, BuildRulePrompt(submodule, current, example, best, worst)),
            };
            try
            {
                var response = await HookedLmCall.InvokeAsync(
                    context with { Chat = owner._reflectionClient },
                    "SIMBA.Rule", messages, cancellationToken: ct).ConfigureAwait(false);
                return response.Text?.Trim() ?? "";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A timeout cancels the call without cancelling ct; it is a failure, not a stop.
                throw new OptimizerException($"SIMBA reflection failed for submodule '{submodule}'.", ex);
            }
        }

        async Task<List<RunRecord>> EvaluateAsync(
            Candidate candidate, IReadOnlyList<Example> examples, CancellationToken ct)
        {
            var bound = new OptimizedProgram<TProgram>(program, candidate.Demos, candidate.Instructions).Bind(context);
            var records = new List<RunRecord>(examples.Count);
            foreach (var example in examples)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var actual = (await owner._programRunner(bound, example, context, ct).ConfigureAwait(false)).Outputs;
                    double score = metric.Score(actual, example.Outputs, _placeholderMeta);
                    // NaN would break the ranking and the weighted draw.
                    records.Add(new RunRecord(actual, double.IsNaN(score) ? 0.0 : score, null));
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // A timeout cancels the call without cancelling ct; it scores 0 like any other failure.
                    Errors++;
                    records.Add(new RunRecord(null, 0.0, e.Message));
                }
            }
            return records;
        }

        void Report(string phase, int step, double best) =>
            progress?.Report(new OptimizerProgress(
                Phase: phase,
                Step: step,
                TotalSteps: _options.MaxSteps,
                BestScore: best,
                Extras: new Dictionary<string, object?>
                {
                    ["RunnerErrors"] = Errors,
                    ["Candidates"] = _pool.Count,
                }));
    }
}
