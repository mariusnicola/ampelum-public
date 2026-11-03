using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>A score in [0, 1] for one example, with the text that explains it to the reflection model.</summary>
/// <param name="Score">The score of the example.</param>
/// <param name="Feedback">What was right or wrong about the actual outputs.</param>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed record FeedbackScore(double Score, string Feedback);

/// <summary>
/// A metric that explains its score. Pass an instance that also implements <see cref="IMetric"/> to
/// <see cref="GEPAOptimizer{TProgram}"/>; a plain <see cref="IMetric"/> works too, and then the feedback
/// is built from the expected and the actual outputs.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
public interface IFeedbackMetric
{
    /// <summary>Scores <paramref name="actual"/> against <paramref name="expected"/> and says why.</summary>
    /// <param name="actual">The outputs the program produced (a dictionary of output fields).</param>
    /// <param name="expected">The expected outputs (a dictionary of output fields).</param>
    /// <param name="signature">Placeholder metadata; the optimizer does not know the signature of a composed program.</param>
    FeedbackScore ScoreWithFeedback(object actual, object expected, SignatureMetadata signature);
}

/// <summary>
/// GEPA prompt optimizer (Agrawal et al. 2025, as in <c>dspy.GEPA</c>). It evolves a pool of candidates,
/// each a set of instruction overrides, by reflecting on what the program did.
/// <list type="number">
///   <item><description>
///     Pool: candidate 0 is the unchanged program. Every candidate is scored per example on the
///     validation set (the <c>valset</c> argument, else a seeded split of the trainset) and keeps the ids
///     of its parents.
///   </description></item>
///   <item><description>
///     Parent: the Pareto frontier is the set of candidates that are best on at least one validation
///     example. The parent is drawn from it with a seeded RNG, weighted by the number of examples the
///     candidate is best on.
///   </description></item>
///   <item><description>
///     Mutation: the submodule to change is picked round robin. The parent runs on a seeded minibatch of the
///     trainset. The reflection model, called through <see cref="HookedLmCall"/> with a module name
///     starting <c>GEPA.</c>, gets the current instruction and each example's inputs, outputs and feedback,
///     and writes a new instruction. The child runs on the same minibatch; only if it scores higher than the
///     parent does it run on the whole validation set and join the pool.
///   </description></item>
///   <item><description>
///     Merge (when enabled): every <see cref="GepaOptions.MergeEvery"/> iterations two frontier candidates
///     that share an ancestor, neither being the ancestor of the other, are combined. Per submodule the child
///     takes the instruction of the side that changed it since the closest common ancestor (the side with the
///     better validation mean when both did). It is gated like a mutation child, against the better parent.
///   </description></item>
/// </list>
/// The search stops after <see cref="GepaOptions.MaxIterations"/> iterations or once
/// <see cref="GepaOptions.MaxMetricCalls"/> metric calls are spent. No example run starts once the cap is
/// reached, and a candidate whose evaluation was cut short is dropped, so the result is the best fully
/// evaluated candidate. <c>CompileAsync</c> throws <see cref="ArgumentException"/> when the cap is smaller than
/// one run of the validation set. The result is the candidate with the best mean
/// validation score (the earliest on a tie), returned as instruction overrides: bind it with
/// <see cref="OptimizedProgram{TProgram}.Bind"/>.
/// A runner or metric exception on one example scores 0 for that example and is counted (activity tag
/// <c>optimizer.runner_errors</c> and the <c>RunnerErrors</c> extra of every progress report); a NaN score counts
/// as 0. Only cancellation of the token passed to <c>CompileAsync</c> propagates: a timeout inside the runner or
/// the reflection call, which cancels that call but not the token, is an error like any other.
/// A failing reflection call throws <see cref="OptimizerException"/>.
/// The same seed with a deterministic model gives the same result.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
[Experimental("AMPELUM_V09_PREVIEW")]
public sealed class GEPAOptimizer<TProgram> : IOptimizer<TProgram>
{
    const string SingleName = "__single__";
    const int ParentSalt = 40;
    const int MinibatchSalt = 1_000_000;
    const int SplitSalt = 50;

    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> _programRunner;
    readonly IChatClient _reflectionClient;
    readonly GepaOptions _options;
    readonly IReadOnlyList<Example>? _valset;

    /// <summary>Construct a GEPA optimizer.</summary>
    /// <param name="programRunner">Runs the program against an example, returning the produced outputs.</param>
    /// <param name="reflectionClient">Chat client that rewrites an instruction from the records of a run.</param>
    /// <param name="options">Budget, minibatch, merge and seed settings. Default: <see cref="GepaOptions"/> defaults.</param>
    /// <param name="valset">Validation set. Default: a seeded split of the trainset passed to <c>CompileAsync</c>.</param>
    public GEPAOptimizer(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> programRunner,
        IChatClient reflectionClient,
        GepaOptions? options = null,
        IReadOnlyList<Example>? valset = null)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentNullException.ThrowIfNull(reflectionClient);
        options ??= new GepaOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxMetricCalls);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinibatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MergeEvery, 1);
        if (valset is { Count: 0 })
            throw new ArgumentException("Valset must contain at least one example.", nameof(valset));
        _programRunner = programRunner;
        _reflectionClient = reflectionClient;
        _options = options;
        _valset = valset;
    }

    /// <summary>The settings this optimizer runs with.</summary>
    public GepaOptions Options => _options;

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
        activity?.SetTag("optimizer.type", nameof(GEPAOptimizer<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var names = program is IComposedModule { Submodules.Count: > 0 } composed
            ? composed.Submodules.Keys.ToList()
            : new List<string> { SingleName };
        var (train, valid) = Split(trainset);
        if (_options.MaxMetricCalls < valid.Count)
            throw new ArgumentException(
                $"MaxMetricCalls ({_options.MaxMetricCalls}) must cover one run of the {valid.Count} validation examples.",
                nameof(trainset));

        var run = new Run(this, program, metric, context, names, train, valid, progress);
        var best = await run.SearchAsync(ct).ConfigureAwait(false);

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(GEPAOptimizer<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(GEPAOptimizer<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(best.Mean,
            new KeyValuePair<string, object?>("optimizer.type", nameof(GEPAOptimizer<TProgram>)));

        activity?.SetTag("optimizer.final_score", best.Mean);
        activity?.SetTag("optimizer.runner_errors", run.Errors);
        activity?.SetTag("optimizer.metric_calls", run.MetricCalls);

        return new OptimizedProgram<TProgram>(
            program, new Dictionary<string, IReadOnlyList<Example>>(), best.Instructions)
        {
            Score = best.Mean,
            TrainsetSize = trainset.Count,
        };
    }

    (IReadOnlyList<Example> Train, IReadOnlyList<Example> Valid) Split(IReadOnlyList<Example> trainset)
    {
        if (_valset is not null) return (trainset, _valset);
        // One example cannot be split: it trains and validates.
        if (trainset.Count == 1) return (trainset, trainset);

        var shuffled = Shuffle(trainset, Derive(_options.Seed, SplitSalt));
        int validCount = Math.Clamp(trainset.Count * _options.ValidationHoldoutPercent / 100, 1, trainset.Count - 1);
        return (shuffled.Skip(validCount).ToList(), shuffled.Take(validCount).ToList());
    }

    // ---- frontier, parents, merge (internal so tests can drive them with hand-built tables) -----

    /// <summary>
    /// Per candidate, the number of validation examples it is best on. A candidate tied for best on an
    /// example counts for it. A candidate with a weight above zero is on the Pareto frontier.
    /// </summary>
    internal static int[] FrontierWeights(IReadOnlyList<IReadOnlyList<double>> scores)
    {
        var weights = new int[scores.Count];
        for (int j = 0; j < scores[0].Count; j++)
        {
            double best = scores.Max(s => s[j]);
            for (int i = 0; i < scores.Count; i++)
                if (scores[i][j] >= best) weights[i]++;
        }
        return weights;
    }

    /// <summary>Draws a candidate index with probability proportional to its weight.</summary>
    internal static int PickParent(int[] weights, Random rng)
    {
        int pick = rng.Next(weights.Sum());
        int index = 0;
        for (; pick >= weights[index]; index++) pick -= weights[index];
        return index;
    }

    /// <summary>
    /// Per submodule, the instruction of the side that changed it since <paramref name="ancestor"/>; the side with
    /// the better validation mean when both did. A submodule neither side changed keeps the ancestor's instruction.
    /// </summary>
    internal static Dictionary<string, string> MergeInstructions(
        Candidate a, Candidate b, Candidate ancestor, IReadOnlyList<string> names)
    {
        var merged = new Dictionary<string, string>();
        foreach (var name in names)
        {
            var origin = ancestor.Instructions.GetValueOrDefault(name);
            var fromA = a.Instructions.GetValueOrDefault(name);
            var fromB = b.Instructions.GetValueOrDefault(name);
            var value = fromA != origin && fromB != origin ? (a.Mean >= b.Mean ? fromA : fromB)
                : fromA != origin ? fromA
                : fromB;
            if (value is not null) merged[name] = value;
        }
        return merged;
    }

    /// <summary>Ids of every candidate the given one descends from.</summary>
    internal static HashSet<int> AncestorsOf(IReadOnlyList<Candidate> pool, Candidate candidate)
    {
        var found = new HashSet<int>();
        var open = new Stack<int>(candidate.Parents);
        while (open.Count > 0)
        {
            int id = open.Pop();
            if (!found.Add(id)) continue;
            foreach (var parent in pool[id].Parents) open.Push(parent);
        }
        return found;
    }

    internal static string BuildReflectionPrompt(
        string submodule, string instruction, IReadOnlyList<EvalRecord> records)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Module: ").Append(submodule).Append('\n');
        sb.Append("Current instruction:\n").Append(instruction).Append("\n\n");
        sb.Append("Records from running the program:\n");
        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            sb.Append("Record ").Append(i + 1).Append(":\n");
            sb.Append("Inputs: ").Append(RenderFields(record.Example.Inputs)).Append('\n');
            sb.Append("Outputs: ").Append(record.Actual is null ? "(none)" : RenderFields(record.Actual)).Append('\n');
            sb.Append("Score: ").Append(record.Score.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Feedback: ").Append(record.Feedback).Append("\n\n");
        }
        sb.Append("Write a new instruction for this module that fixes what the feedback criticizes and keeps what works.");
        return sb.ToString();
    }

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

    /// <summary>One member of the candidate pool.</summary>
    internal sealed class Candidate(
        int id, IReadOnlyDictionary<string, string> instructions, IReadOnlyList<int> parents, double[] scores)
    {
        public int Id { get; } = id;
        /// <summary>Instruction overrides by submodule name; a submodule not listed keeps its own instruction.</summary>
        public IReadOnlyDictionary<string, string> Instructions { get; } = instructions;
        public IReadOnlyList<int> Parents { get; } = parents;
        /// <summary>Score on each validation example, in validation order.</summary>
        public double[] Scores { get; } = scores;
        public double Mean => Scores.Average();
    }

    /// <summary>What running one example gave: the outputs (null when the run failed), the score and the feedback.</summary>
    internal sealed record EvalRecord(
        Example Example, IReadOnlyDictionary<string, object?>? Actual, double Score, string Feedback);

    /// <summary>Raised when an example run would exceed <see cref="GepaOptions.MaxMetricCalls"/>.</summary>
    sealed class BudgetExhausted : Exception;

    sealed class Run(
        GEPAOptimizer<TProgram> owner,
        TProgram program,
        IMetric metric,
        AmpelumContext context,
        List<string> names,
        IReadOnlyList<Example> train,
        IReadOnlyList<Example> valid,
        IProgress<OptimizerProgress>? progress)
    {
        readonly GepaOptions _options = owner._options;
        readonly List<Candidate> _pool = [];
        readonly HashSet<(int, int)> _mergedPairs = [];
        int _mutations;
        int _merges;

        public int Errors { get; private set; }
        public int MetricCalls { get; private set; }

        public async Task<Candidate> SearchAsync(CancellationToken ct)
        {
            var empty = new Dictionary<string, string>();
            var baseline = await EvaluateAsync(empty, valid, ct).ConfigureAwait(false);
            _pool.Add(new Candidate(0, empty, [], baseline.Select(r => r.Score).ToArray()));
            Report("baseline", 0);

            var rng = new Random(Derive(_options.Seed, ParentSalt));
            for (int iteration = 1; iteration <= _options.MaxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                var minibatch = Shuffle(train, Derive(_options.Seed, MinibatchSalt + iteration))
                    .Take(_options.MinibatchSize).ToList();
                var weights = FrontierWeights(_pool.Select(c => (IReadOnlyList<double>)c.Scores).ToList());

                try
                {
                    bool merged = _options.UseMerge
                                  && iteration % _options.MergeEvery == 0
                                  && await TryMergeAsync(weights, minibatch, rng, ct).ConfigureAwait(false);
                    if (!merged)
                        await MutateAsync(_pool[PickParent(weights, rng)], minibatch, ct).ConfigureAwait(false);
                }
                catch (BudgetExhausted)
                {
                    // The candidate being evaluated was never added to the pool, so only whole evaluations count.
                    break;
                }

                Report("iteration", iteration);
            }

            return _pool.MaxBy(c => c.Mean)!;
        }

        async Task MutateAsync(Candidate parent, List<Example> minibatch, CancellationToken ct)
        {
            var submodule = names[_mutations++ % names.Count];
            var parentRecords = await EvaluateAsync(parent.Instructions, minibatch, ct).ConfigureAwait(false);
            var current = CurrentInstruction(submodule, parent.Instructions);

            var proposal = await ReflectAsync(submodule, current, parentRecords, ct).ConfigureAwait(false);
            if (proposal.Length == 0) return;

            var instructions = new Dictionary<string, string>(parent.Instructions) { [submodule] = proposal };
            var childRecords = await EvaluateAsync(instructions, minibatch, ct).ConfigureAwait(false);
            if (childRecords.Average(r => r.Score) > parentRecords.Average(r => r.Score))
                await AddAsync(instructions, [parent.Id], ct).ConfigureAwait(false);
        }

        async Task<bool> TryMergeAsync(int[] weights, List<Example> minibatch, Random rng, CancellationToken ct)
        {
            var frontier = _pool.Where(c => weights[c.Id] > 0).ToList();
            var pairs = new List<(Candidate A, Candidate B, Dictionary<string, string> Merged)>();
            foreach (var a in frontier)
            foreach (var b in frontier.Where(b => b.Id > a.Id))
            {
                var ancestorsA = AncestorsOf(_pool, a);
                var ancestorsB = AncestorsOf(_pool, b);
                if (ancestorsA.Contains(b.Id) || ancestorsB.Contains(a.Id) || _mergedPairs.Contains((a.Id, b.Id)))
                    continue;

                var ancestor = _pool[ancestorsA.Intersect(ancestorsB).Max()];
                var merged = MergeInstructions(a, b, ancestor, names);
                // A merge equal to one of its parents adds nothing.
                if (!SameInstructions(merged, a.Instructions) && !SameInstructions(merged, b.Instructions))
                    pairs.Add((a, b, merged));
            }
            if (pairs.Count == 0) return false;

            var chosen = pairs[rng.Next(pairs.Count)];
            _mergedPairs.Add((chosen.A.Id, chosen.B.Id));
            var recordsA = await EvaluateAsync(chosen.A.Instructions, minibatch, ct).ConfigureAwait(false);
            var recordsB = await EvaluateAsync(chosen.B.Instructions, minibatch, ct).ConfigureAwait(false);
            var recordsChild = await EvaluateAsync(chosen.Merged, minibatch, ct).ConfigureAwait(false);
            double bar = Math.Max(recordsA.Average(r => r.Score), recordsB.Average(r => r.Score));
            if (recordsChild.Average(r => r.Score) > bar)
            {
                await AddAsync(chosen.Merged, [chosen.A.Id, chosen.B.Id], ct).ConfigureAwait(false);
                _merges++;
            }
            return true;
        }

        async Task AddAsync(Dictionary<string, string> instructions, int[] parents, CancellationToken ct)
        {
            var records = await EvaluateAsync(instructions, valid, ct).ConfigureAwait(false);
            _pool.Add(new Candidate(_pool.Count, instructions, parents, records.Select(r => r.Score).ToArray()));
        }

        static bool SameInstructions(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

        string CurrentInstruction(string submodule, IReadOnlyDictionary<string, string> overrides) =>
            OptimizerInstructions.Current(program!, submodule, overrides);

        async Task<string> ReflectAsync(
            string submodule, string current, IReadOnlyList<EvalRecord> records, CancellationToken ct)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a prompt engineer. You improve one instruction of a language model program from " +
                    "records of its runs. Reply with the new instruction text only."),
                new(ChatRole.User, BuildReflectionPrompt(submodule, current, records)),
            };
            try
            {
                var response = await HookedLmCall.InvokeAsync(
                    context with { Chat = owner._reflectionClient },
                    "GEPA.Reflect", messages, cancellationToken: ct).ConfigureAwait(false);
                return response.Text?.Trim() ?? "";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A timeout cancels the call without cancelling ct; it is a failure, not a stop.
                throw new OptimizerException($"GEPA reflection failed for submodule '{submodule}'.", ex);
            }
        }

        async Task<List<EvalRecord>> EvaluateAsync(
            IReadOnlyDictionary<string, string> instructions, IReadOnlyList<Example> examples, CancellationToken ct)
        {
            var bound = new OptimizedProgram<TProgram>(
                program, new Dictionary<string, IReadOnlyList<Example>>(), instructions).Bind(context);
            var records = new List<EvalRecord>(examples.Count);
            foreach (var example in examples)
            {
                ct.ThrowIfCancellationRequested();
                if (MetricCalls >= _options.MaxMetricCalls) throw new BudgetExhausted();
                MetricCalls++;
                IReadOnlyDictionary<string, object?>? actual = null;
                try
                {
                    actual = (await owner._programRunner(bound, example, context, ct).ConfigureAwait(false)).Outputs;
                    var scored = metric is IFeedbackMetric explaining
                        ? explaining.ScoreWithFeedback(actual, example.Outputs, _placeholderMeta)
                        : PlainScore(actual, example);
                    // NaN would break the frontier comparison and the weighted draw.
                    records.Add(new EvalRecord(
                        example, actual, double.IsNaN(scored.Score) ? 0.0 : scored.Score, scored.Feedback));
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // A timeout cancels the call without cancelling ct; it scores 0 like any other failure.
                    Errors++;
                    records.Add(new EvalRecord(example, actual, 0.0, $"The run failed: {e.Message}"));
                }
            }
            return records;
        }

        FeedbackScore PlainScore(IReadOnlyDictionary<string, object?> actual, Example example)
        {
            double score = metric.Score(actual, example.Outputs, _placeholderMeta);
            return new FeedbackScore(
                score,
                $"Expected {RenderFields(example.Outputs)}, got {RenderFields(actual)}.");
        }

        void Report(string phase, int step)
        {
            var weights = FrontierWeights(_pool.Select(c => (IReadOnlyList<double>)c.Scores).ToList());
            progress?.Report(new OptimizerProgress(
                Phase: phase,
                Step: step,
                TotalSteps: _options.MaxIterations,
                BestScore: _pool.Max(c => c.Mean),
                Extras: new Dictionary<string, object?>
                {
                    ["RunnerErrors"] = Errors,
                    ["MetricCalls"] = MetricCalls,
                    ["Candidates"] = _pool.Count,
                    ["FrontierSize"] = weights.Count(w => w > 0),
                    ["Merges"] = _merges,
                }));
        }
    }
}

/// <summary>Settings of <see cref="GEPAOptimizer{TProgram}"/>.</summary>
public sealed record GepaOptions
{
    /// <summary>Maximum search iterations (a mutation or a merge each). Default 10.</summary>
    public int MaxIterations { get; init; } = 10;

    /// <summary>
    /// Maximum metric calls, one per example run, counting every run of the search. No run starts once the count
    /// has reached it. It must be at least the size of the validation set. Default 200.
    /// </summary>
    public int MaxMetricCalls { get; init; } = 200;

    /// <summary>Trainset examples in the minibatch a child is compared on. Default 3.</summary>
    public int MinibatchSize { get; init; } = 3;

    /// <summary>Whether to merge frontier candidates that share an ancestor. Default true.</summary>
    public bool UseMerge { get; init; } = true;

    /// <summary>A merge is tried at every this many iterations. Default 3.</summary>
    public int MergeEvery { get; init; } = 3;

    /// <summary>Percentage of the trainset held out for validation when no valset is given. Default 20.</summary>
    public int ValidationHoldoutPercent { get; init; } = 20;

    /// <summary>Seed for the split, the minibatches and every draw. Default 0.</summary>
    public int Seed { get; init; }
}
