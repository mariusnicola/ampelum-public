using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;

namespace SignalGrid.Ampelum.Optimizers;

/// <summary>Fixed tips the instruction proposer is nudged with; one is picked per proposal by the seeded RNG.</summary>
internal static class MiproV2Tips
{
    internal static readonly string[] All =
    [
        "Be concise and direct.",
        "State the expected output format exactly.",
        "Say how to handle ambiguous or borderline inputs.",
        "Tell the model what to avoid.",
        "Name the task and who the answer is for.",
        "Ask the model to check the edge cases before it answers.",
    ];
}

/// <summary>
/// MIPROv2 prompt optimizer. It builds two pools of candidates per submodule and searches the
/// combinations with a seeded Tree-structured Parzen Estimator.
/// <list type="number">
///   <item><description>
///     Demo candidates: <c>numDemoSets</c> bootstrapped demo sets, each from
///     <see cref="BootstrapFewShot{TProgram}"/> over a seed-shuffled trainset, plus the empty
///     (zero-shot) set as candidate 0.
///   </description></item>
///   <item><description>
///     Instruction candidates: the current instruction as candidate 0, plus
///     <c>candidatesPerSubmodule</c> proposals. One proposer call first summarizes a sample of the
///     trainset. Each proposal prompt carries that summary, the submodule name, one candidate demo
///     set rendered as examples and one tip picked by the seeded RNG. Every proposer call runs
///     through <see cref="Hooks.HookedLmCall"/> with a module name starting <c>MIPROv2.</c>.
///   </description></item>
///   <item><description>
///     Search: each trial picks one instruction and one demo set per submodule. The first
///     <c>max(1, numTrials / 5)</c> trials are random, the rest come from the TPE sampler. A trial
///     is scored on a seeded random minibatch of the validation set.
///   </description></item>
///   <item><description>
///     Full evaluation: every <c>fullEvalEvery</c> trials, and at the last trial, the combination
///     with the best mean minibatch score is scored on the whole validation set. The returned
///     program is the best full-evaluation combination.
///   </description></item>
/// </list>
/// A runner or metric exception on one example scores 0 for that example and is counted (activity
/// tag <c>optimizer.runner_errors</c> and the <c>RunnerErrors</c> extra of every progress report).
/// Cancellation propagates. The same seed with a deterministic model gives the same result.
/// </summary>
/// <typeparam name="TProgram">The user program type.</typeparam>
public sealed class MiproV2<TProgram> : IOptimizer<TProgram>
    where TProgram : class
{
    const string SingleName = "__single__";
    const int SummarySampleSize = 10;
    const int DemoSalt = 1_000;
    const int SummarySalt = 10;
    const int TipSalt = 20;
    const int TpeSalt = 30;
    const int MinibatchSalt = 1_000_000;

    static readonly SignatureMetadata _placeholderMeta =
        new(typeof(object), "", [], []);

    readonly Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> _programRunner;
    readonly IChatClient _proposerClient;
    readonly int _maxRounds;
    readonly int _candidatesPerSubmodule;
    readonly int _maxBootstrappedDemos;
    readonly int _numTrials;
    readonly int _numDemoSets;
    readonly int _minibatchSize;
    readonly int _fullEvalEvery;
    readonly int _seed;
    readonly IReadOnlyList<Example>? _valset;
    readonly double _metricThreshold;

    /// <summary>Search trials used when <c>numTrials</c> is not given.</summary>
    public int MaxRounds => _maxRounds;

    /// <summary>Instruction proposals per submodule (the current instruction is one more candidate).</summary>
    public int CandidatesPerSubmodule => _candidatesPerSubmodule;

    /// <summary>Maximum bootstrapped demos per demo set.</summary>
    public int MaxBootstrappedDemos => _maxBootstrappedDemos;

    /// <summary>Number of search trials.</summary>
    public int NumTrials => _numTrials;

    /// <summary>Number of bootstrapped demo sets per submodule (the empty set is one more candidate).</summary>
    public int NumDemoSets => _numDemoSets;

    /// <summary>Examples in each trial's minibatch.</summary>
    public int MinibatchSize => _minibatchSize;

    /// <summary>A full validation run happens every this many trials, and at the last trial.</summary>
    public int FullEvalEvery => _fullEvalEvery;

    /// <summary>Seed for every random choice: shuffles, tips, the sampler and the minibatches.</summary>
    public int Seed => _seed;

    /// <summary>Lowest metric score a run needs for its traces to become demos.</summary>
    public double MetricThreshold => _metricThreshold;

    /// <summary>Construct a MIPROv2 optimizer.</summary>
    /// <param name="programRunner">Runs the program against an example, returning the produced outputs.</param>
    /// <param name="instructionProposerClient">Chat client that writes the dataset summary and the instruction proposals.</param>
    /// <param name="maxRounds">Number of search trials when <paramref name="numTrials"/> is null.</param>
    /// <param name="candidatesPerSubmodule">Instruction proposals per submodule.</param>
    /// <param name="maxBootstrappedDemos">Demo cap per bootstrapped demo set.</param>
    /// <param name="numTrials">Search trials. Default: the value of <paramref name="maxRounds"/>.</param>
    /// <param name="numDemoSets">Bootstrapped demo sets per submodule. Default 3.</param>
    /// <param name="minibatchSize">Examples scored per trial. Default 25; a smaller validation set is used whole.</param>
    /// <param name="fullEvalEvery">Full validation run period in trials. Default 5.</param>
    /// <param name="seed">Seed for every random choice. Default 0.</param>
    /// <param name="valset">Validation set. Default: the trainset passed to <c>CompileAsync</c>.</param>
    /// <param name="metricThreshold">Lowest metric score a run needs for its traces to become demos in the bootstrapped demo sets. Default 1.0, for a metric in [0, 1]; use a lower value for a metric on another scale.</param>
    public MiproV2(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> programRunner,
        IChatClient instructionProposerClient,
        int maxRounds = 3,
        int candidatesPerSubmodule = 3,
        int maxBootstrappedDemos = 4,
        int? numTrials = null,
        int numDemoSets = 3,
        int minibatchSize = 25,
        int fullEvalEvery = 5,
        int seed = 0,
        IReadOnlyList<Example>? valset = null,
        double metricThreshold = 1.0)
    {
        ArgumentNullException.ThrowIfNull(programRunner);
        ArgumentNullException.ThrowIfNull(instructionProposerClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidatesPerSubmodule, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBootstrappedDemos);
        ArgumentOutOfRangeException.ThrowIfLessThan(numTrials ?? maxRounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numDemoSets, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minibatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fullEvalEvery, 1);
        _programRunner = programRunner;
        _proposerClient = instructionProposerClient;
        _maxRounds = maxRounds;
        _candidatesPerSubmodule = candidatesPerSubmodule;
        _maxBootstrappedDemos = maxBootstrappedDemos;
        _numTrials = numTrials ?? maxRounds;
        _numDemoSets = numDemoSets;
        _minibatchSize = minibatchSize;
        _fullEvalEvery = fullEvalEvery;
        _seed = seed;
        _valset = valset;
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
        activity?.SetTag("optimizer.type", nameof(MiproV2<TProgram>));
        activity?.SetTag("optimizer.trainset_size", trainset.Count);

        var sw = Stopwatch.StartNew();

        var names = (program is IComposedModule composed)
            ? composed.Submodules.Keys.ToList()
            : new List<string> { SingleName };
        var valset = _valset ?? trainset;

        var demoCandidates = await BuildDemoCandidatesAsync(
            program, trainset, metric, context, names, ct).ConfigureAwait(false);
        var instructionCandidates = await BuildInstructionCandidatesAsync(
            trainset, context, names, demoCandidates, ct).ConfigureAwait(false);

        // Parameter 2i is submodule i's instruction, parameter 2i+1 is its demo set.
        var categories = new List<int>(names.Count * 2);
        for (int i = 0; i < names.Count; i++)
        {
            categories.Add(instructionCandidates[i].Count);
            categories.Add(demoCandidates[i].Count);
        }

        OptimizedProgram<TProgram> Build(int[] choice)
        {
            var demos = new Dictionary<string, IReadOnlyList<Example>>();
            var instructions = new Dictionary<string, string>();
            for (int i = 0; i < names.Count; i++)
            {
                if (choice[2 * i] > 0) instructions[names[i]] = instructionCandidates[i][choice[2 * i]]!;
                var set = demoCandidates[i][choice[2 * i + 1]];
                if (set.Count > 0) demos[names[i]] = set;
            }
            return new OptimizedProgram<TProgram>(program, demos, instructions)
            {
                TrainsetSize = trainset.Count,
            };
        }

        var scorer = new Scorer(_programRunner, metric, context);
        var sampler = new TpeSampler(categories, RandomTrials(_numTrials), Derive(_seed, TpeSalt));
        var finished = new List<(int[] Choice, double Score)>();
        var stats = new OrderedDictionary<string, ComboStat>();
        int[]? fullChoice = null;
        double fullScore = 0.0;
        bool fullDone = false;

        for (int trial = 1; trial <= _numTrials; trial++)
        {
            ct.ThrowIfCancellationRequested();

            var choice = sampler.Suggest(finished);
            var minibatch = Shuffle(valset, Derive(_seed, MinibatchSalt + trial)).Take(_minibatchSize).ToList();
            double score = await scorer.ScoreAsync(Build(choice), minibatch, ct).ConfigureAwait(false);
            finished.Add((choice, score));

            var key = string.Join(',', choice);
            if (!stats.TryGetValue(key, out var stat))
                stats[key] = stat = new ComboStat(choice);
            stat.Sum += score;
            stat.Count++;

            if (trial % _fullEvalEvery == 0 || trial == _numTrials)
            {
                var top = stats.GetAt(0).Value;
                foreach (var candidate in stats.Values)
                    if (candidate.Mean > top.Mean) top = candidate;

                double full = await scorer.ScoreAsync(Build(top.Choice), valset, ct).ConfigureAwait(false);
                if (!fullDone || full > fullScore)
                {
                    fullDone = true;
                    fullScore = full;
                    fullChoice = top.Choice;
                }
            }

            double reported = fullDone ? fullScore : stats.Values.Max(s => s.Mean);
            progress?.Report(new OptimizerProgress(
                Phase: "trial",
                Step: trial,
                TotalSteps: _numTrials,
                BestScore: reported,
                Extras: new Dictionary<string, object?> { ["RunnerErrors"] = scorer.Errors }));
        }

        sw.Stop();
        Diagnostics.OptimizerCompileDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));
        Diagnostics.OptimizerCompileExamples.Record(trainset.Count,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));
        Diagnostics.OptimizerCompileScore.Record(fullScore,
            new KeyValuePair<string, object?>("optimizer.type", nameof(MiproV2<TProgram>)));

        activity?.SetTag("optimizer.final_score", fullScore);
        activity?.SetTag("optimizer.runner_errors", scorer.Errors);

        return Build(fullChoice!) with { Score = fullScore };
    }

    async Task<List<List<IReadOnlyList<Example>>>> BuildDemoCandidatesAsync(
        TProgram program,
        IReadOnlyList<Example> trainset,
        IMetric metric,
        AmpelumContext context,
        List<string> names,
        CancellationToken ct)
    {
        // Candidate 0 of every submodule is the empty (zero-shot) set.
        var candidates = names.Select(_ => new List<IReadOnlyList<Example>> { Array.Empty<Example>() }).ToList();

        // BootstrapFewShot reads the trace sink wired into the context, not the returned Example.
        var bootstrapper = new BootstrapFewShot<TProgram>(
            async (prog, ex, runCtx, runCt) =>
            {
                _ = await _programRunner(prog, ex, runCtx, runCt).ConfigureAwait(false);
            },
            maxBootstrappedDemos: _maxBootstrappedDemos,
            metricThreshold: _metricThreshold);

        for (int d = 0; d < _numDemoSets; d++)
        {
            var shuffled = Shuffle(trainset, Derive(_seed, DemoSalt + d));
            var set = await bootstrapper.CompileAsync(
                program, shuffled, metric, context, null, ct).ConfigureAwait(false);
            for (int i = 0; i < names.Count; i++)
                candidates[i].Add(DemosFor(set, names[i]));
        }
        return candidates;
    }

    async Task<List<List<string?>>> BuildInstructionCandidatesAsync(
        IReadOnlyList<Example> trainset,
        AmpelumContext context,
        List<string> names,
        List<List<IReadOnlyList<Example>>> demoCandidates,
        CancellationToken ct)
    {
        var proposerContext = context with { Chat = _proposerClient };

        var sample = Shuffle(trainset, Derive(_seed, SummarySalt)).Take(SummarySampleSize);
        var summary = await ProposeAsync(
            proposerContext,
            "MIPROv2.SummarizeDataset",
            "You summarize datasets for a prompt engineer. Reply with two or three sentences.",
            "Describe what these examples have in common and what the task is:\n" + RenderExamples(sample),
            "dataset summary",
            ct).ConfigureAwait(false);

        var tipRng = new Random(Derive(_seed, TipSalt));
        // Candidate 0 of every submodule is its current instruction (null: no override).
        var candidates = names.Select(_ => new List<string?> { null }).ToList();
        for (int i = 0; i < names.Count; i++)
        {
            for (int c = 0; c < _candidatesPerSubmodule; c++)
            {
                ct.ThrowIfCancellationRequested();

                // Only bootstrapped sets (index 1 and up) are shown as examples.
                var demoSet = demoCandidates[i][1 + (c % _numDemoSets)];
                var tip = MiproV2Tips.All[tipRng.Next(MiproV2Tips.All.Length)];
                var proposal = await ProposeAsync(
                    proposerContext,
                    "MIPROv2.ProposeInstruction",
                    "You are a prompt engineer. Write one improved instruction for a language model module. " +
                    "Reply with the instruction text only.",
                    $"Dataset summary: {summary}\n" +
                    $"Module: {names[i]}\n" +
                    $"Examples:\n{RenderExamples(demoSet)}\n" +
                    $"Tip: {tip}\n" +
                    "Write the instruction.",
                    $"instruction proposal for submodule '{names[i]}'",
                    ct).ConfigureAwait(false);

                if (proposal.Length > 0) candidates[i].Add(proposal);
            }
        }
        return candidates;
    }

    static async Task<string> ProposeAsync(
        AmpelumContext proposerContext,
        string moduleName,
        string system,
        string user,
        string what,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user),
        };
        try
        {
#pragma warning disable AMPELUM_V09_PREVIEW
            var response = await HookedLmCall.InvokeAsync(
                proposerContext, moduleName, messages, cancellationToken: ct).ConfigureAwait(false);
#pragma warning restore AMPELUM_V09_PREVIEW
            return response.Text?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new OptimizerException($"MIPROv2 proposer failed for {what}.", ex);
        }
    }

    static IReadOnlyList<Example> DemosFor(OptimizedProgram<TProgram> set, string name)
    {
        if (set.DemosBySubmodule.TryGetValue(name, out var demos)) return demos;
        // A program without named submodules gets whatever demo list the traces were filed under.
        return name == SingleName
            ? set.DemosBySubmodule.Values.FirstOrDefault() ?? []
            : [];
    }

    static string RenderExamples(IEnumerable<Example> examples)
    {
        var lines = examples.Select(e => $"Input: {RenderFields(e.Inputs)} | Output: {RenderFields(e.Outputs)}").ToList();
        return lines.Count == 0 ? "(none)" : string.Join('\n', lines);
    }

    static string RenderFields(IReadOnlyDictionary<string, object?> fields) =>
        string.Join(", ", fields.Select(kv => $"{kv.Key}={Convert.ToString(kv.Value, CultureInfo.InvariantCulture)}"));

    internal static int RandomTrials(int numTrials) => Math.Max(1, numTrials / 5);

    static int Derive(int seed, int salt) => unchecked(seed * 7919 + salt);

    internal static IReadOnlyList<Example> Shuffle(IReadOnlyList<Example> list, int seed)
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

    sealed class ComboStat(int[] choice)
    {
        public int[] Choice { get; } = choice;
        public double Sum;
        public int Count;
        public double Mean => Sum / Count;
    }

    sealed class Scorer(
        Func<TProgram, Example, AmpelumContext, CancellationToken, Task<Example>> runner,
        IMetric metric,
        AmpelumContext context)
    {
        public int Errors { get; private set; }

        public async Task<double> ScoreAsync(
            OptimizedProgram<TProgram> optimized,
            IReadOnlyList<Example> examples,
            CancellationToken ct)
        {
            if (examples.Count == 0) return 0.0;
            var bound = optimized.Bind(context);
            double sum = 0.0;
            foreach (var ex in examples)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var actual = await runner(bound, ex, context, ct).ConfigureAwait(false);
                    sum += metric.Score(actual.Outputs, ex.Outputs, _placeholderMeta);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Errors++;
                }
            }
            return sum / examples.Count;
        }
    }
}
