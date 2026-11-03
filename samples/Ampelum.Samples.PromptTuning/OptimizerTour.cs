using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.PromptTuning;

/// <summary>What SIMBA changed.</summary>
/// <param name="Baseline">Mean score of the untuned program on the dataset.</param>
/// <param name="Tuned">Mean score of the tuned program on the dataset.</param>
/// <param name="BaselineInstruction">Instruction the untuned program sends.</param>
/// <param name="TunedInstruction">Instruction the tuned program sends.</param>
/// <param name="TunedDemoCount">Demonstrations the tuned program sends for the classifier.</param>
public sealed record SimbaOutcome(
    double Baseline, double Tuned, string BaselineInstruction, string TunedInstruction, int TunedDemoCount);

/// <summary>What BootstrapFinetune changed.</summary>
/// <param name="Baseline">Mean score of the untuned program on the dataset.</param>
/// <param name="Tuned">Mean score of the program bound to the tuned client.</param>
/// <param name="Program">The tuned program.</param>
public sealed record FinetuneOutcome(double Baseline, double Tuned, OptimizedProgram<SentimentProgram> Program);

/// <summary>What BetterTogether did.</summary>
/// <param name="Baseline">Mean score of the untuned program on the dataset.</param>
/// <param name="Phases">The kind of each step, in the order the steps ran.</param>
/// <param name="StepScores">Score after each step.</param>
/// <param name="Score">Score of the program BetterTogether returned.</param>
/// <param name="Program">The program BetterTogether returned.</param>
public sealed record BetterTogetherOutcome(
    double Baseline,
    IReadOnlyList<string> Phases,
    IReadOnlyList<double> StepScores,
    double Score,
    OptimizedProgram<SentimentProgram> Program);

/// <summary>
/// Runs SIMBA, BootstrapFinetune and BetterTogether on the sentiment task. The caller supplies the clients, so
/// <c>--fake</c> and the tests can use scripted ones. Each method scores the untuned program, optimizes it, and
/// scores the result on the same rows it trained on, because the sample has no held-out set.
/// </summary>
/// <param name="data">Labeled rows.</param>
/// <param name="metric">Scores a prediction against its label.</param>
public sealed class OptimizerTour(IReadOnlyList<LabeledExample<SentimentSig>> data, LabelMatch metric)
{
    /// <summary>Seed of every random choice the optimizers make.</summary>
    public const int Seed = 17;

    /// <summary>Rows in each SIMBA minibatch.</summary>
    public const int MinibatchSize = 4;

    /// <summary>Programs SIMBA runs, and candidates it builds, in each step.</summary>
    public const int CandidatesPerStep = 2;

    /// <summary>SIMBA steps.</summary>
    public const int Steps = 2;

    /// <summary>Most demonstrations SIMBA gives the classifier.</summary>
    public const int MaxDemos = 2;

    /// <summary>The strategy BetterTogether runs.</summary>
    public const string Strategy = "p -> w -> p";

    /// <summary>SIMBA: builds candidates from the rows the program is unstable on and keeps the best.</summary>
    /// <param name="chat">Classifier client.</param>
    /// <param name="reflection">Client that writes a rule from the best and the worst run of a row.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SimbaOutcome> RunSimbaAsync(IChatClient chat, IChatClient reflection, CancellationToken ct = default)
    {
        var context = ContextFor(chat);
        var bound = Untuned(context);
        var baseline = await ScoreAsync(bound, ct).ConfigureAwait(false);

        var optimizer = new SimbaOptimizer<SentimentProgram>(
            PromptTuningPipeline.RunOneAsync,
            reflection,
            new SimbaOptions
            {
                Seed = Seed, MinibatchSize = MinibatchSize, CandidatesPerStep = CandidatesPerStep,
                MaxSteps = Steps, MaxDemos = MaxDemos,
            });
        var tuned = await optimizer.CompileAsync(bound, Trainset(), metric, context, ct: ct).ConfigureAwait(false);
        var tunedScore = await ScoreAsync(tuned.Bind(context), ct).ConfigureAwait(false);

        return new SimbaOutcome(
            baseline,
            tunedScore,
            SignatureMetadataCache.For<SentimentSig>().Instructions,
            tuned.InstructionOverrides.GetValueOrDefault(
                SentimentProgram.ClassifierName, SignatureMetadataCache.For<SentimentSig>().Instructions),
            tuned.DemosBySubmodule.TryGetValue(SentimentProgram.ClassifierName, out var demos) ? demos.Count : 0);
    }

    /// <summary>BootstrapFinetune: sends the traces that pass the metric to the provider and binds the tuned client.</summary>
    /// <param name="chat">Classifier client.</param>
    /// <param name="provider">The fine-tuning provider.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<FinetuneOutcome> RunBootstrapFinetuneAsync(
        IChatClient chat, IFineTuneProvider provider, CancellationToken ct = default)
    {
        var context = ContextFor(chat);
        var bound = Untuned(context);
        var baseline = await ScoreAsync(bound, ct).ConfigureAwait(false);

        var optimizer = new BootstrapFinetune<SentimentProgram>(Forward, provider);
        var tuned = await optimizer.CompileAsync(bound, Trainset(), metric, context, ct: ct).ConfigureAwait(false);
        var tunedScore = await ScoreAsync(tuned.Bind(context), ct).ConfigureAwait(false);

        return new FinetuneOutcome(baseline, tunedScore, tuned);
    }

    /// <summary>BetterTogether: prompt, weight and prompt steps, each scored, and the best result returned.</summary>
    /// <param name="chat">Classifier client.</param>
    /// <param name="provider">The fine-tuning provider.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BetterTogetherOutcome> RunBetterTogetherAsync(
        IChatClient chat, IFineTuneProvider provider, CancellationToken ct = default)
    {
        var context = ContextFor(chat);
        var bound = Untuned(context);
        var baseline = await ScoreAsync(bound, ct).ConfigureAwait(false);

        var trainset = Trainset();
        var phases = new List<string>();
        var optimizer = new BetterTogether<SentimentProgram>(
            PromptTuningPipeline.RunOneAsync,
            fineTuneProvider: provider,
            strategy: Strategy,
            valset: trainset,
            seed: Seed);
        var result = await optimizer.CompileAsync(
            bound, trainset, metric, context, new Collect(phases), ct).ConfigureAwait(false);

        return new BetterTogetherOutcome(baseline, phases, result.StepScores!, result.Score!.Value, result);
    }

    sealed class Collect(List<string> phases) : IProgress<OptimizerProgress>
    {
        public void Report(OptimizerProgress value) => phases.Add(value.Phase);
    }

    static AmpelumContext ContextFor(IChatClient chat) =>
        new(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

    // Bind first so the classifier carries its submodule name, which the optimizers need to file traces and overrides.
    static SentimentProgram Untuned(AmpelumContext context) =>
        new OptimizedProgram<SentimentProgram>(
            new SentimentProgram(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>()).Bind(context);

    IReadOnlyList<Example> Trainset() => [.. data.Select(e => Example.From(e.Expected))];

    // The runner of the optimizers that only need the traces.
    static Task Forward(SentimentProgram program, Example example, AmpelumContext context, CancellationToken ct) =>
        PromptTuningPipeline.RunOneAsync(program, example, context, ct);

    // One at a time so a scripted client answers in dataset order.
    async Task<double> ScoreAsync(SentimentProgram program, CancellationToken ct)
    {
        double sum = 0.0;
        foreach (var row in data)
        {
            var prediction = await program.ClassifyAsync(row.Inputs, ct).ConfigureAwait(false);
            sum += metric.Score(prediction.Value, row.Expected, SignatureMetadataCache.For<SentimentSig>());
        }
        return sum / data.Count;
    }
}
