using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Persistence;

namespace Ampelum.Samples.PromptTuning;

/// <summary>Everything the pipeline measured and produced.</summary>
/// <param name="Baseline">Evaluation of the untuned program.</param>
/// <param name="Tuned">Evaluation of the program loaded back from disk.</param>
/// <param name="BaselineInstruction">Instruction the untuned program sends.</param>
/// <param name="TunedInstruction">Instruction read back from the saved file.</param>
/// <param name="TunedDemoCount">Demonstrations read back from the saved file for the classifier.</param>
/// <param name="ProgramPath">Where the tuned program was saved.</param>
/// <param name="ReportMarkdown">A/B comparison as markdown.</param>
/// <param name="ReportJson">A/B comparison as JSON.</param>
public sealed record PromptTuningResult(
    EvaluationResult<SentimentSig> Baseline,
    EvaluationResult<SentimentSig> Tuned,
    string BaselineInstruction,
    string TunedInstruction,
    int TunedDemoCount,
    string ProgramPath,
    string ReportMarkdown,
    string ReportJson);

/// <summary>Measures a prompt on labeled data, tunes it, saves it, reloads it and measures again.</summary>
/// <param name="chat">Chat client the classifier runs on.</param>
/// <param name="proposer">Chat client that writes the dataset summary and the instruction proposals.</param>
/// <param name="datasetPath">Labeled JSONL file.</param>
/// <param name="programPath">Where the tuned program is saved.</param>
/// <param name="persistence">Saves and loads the tuned program.</param>
/// <param name="metric">Scores a prediction against its label.</param>
public sealed class PromptTuningPipeline(
    IChatClient chat,
    IChatClient proposer,
    string datasetPath,
    string programPath,
    IProgramPersistence persistence,
    LabelMatch metric)
{
    /// <summary>Seed of every random choice MIPROv2 makes: shuffles, tips, search and minibatches.</summary>
    public const int OptimizerSeed = 17;

    /// <summary>Search trials.</summary>
    public const int NumTrials = 10;

    /// <summary>Bootstrapped demo sets per submodule.</summary>
    public const int NumDemoSets = 2;

    /// <summary>Demos in each bootstrapped set.</summary>
    public const int BootstrappedDemos = 2;

    /// <summary>Instruction proposals per submodule.</summary>
    public const int InstructionCandidates = 2;

    /// <summary>Rows scored per trial.</summary>
    public const int MinibatchSize = 4;

    /// <summary>A full run over the dataset happens every this many trials, and at the last trial.</summary>
    public const int FullEvalEvery = 4;

    // Fixed so the bootstrap interval in the report is reproducible.
    const int ReportSeed = 17;

    /// <summary>Runs the full flow.</summary>
    public async Task<PromptTuningResult> RunAsync(CancellationToken ct = default)
    {
        var data = await SentimentData.LoadAsync(datasetPath, ct).ConfigureAwait(false);
        var baselineInstruction = SignatureMetadataCache.For<SentimentSig>().Instructions;
        var trainset = data.Select(e => Example.From(e.Expected)).ToList();

        var context = new AmpelumContext(
            chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);

        // Bind first so the classifier carries its submodule name, which the optimizer needs
        // to attach instruction overrides and to file bootstrapped traces.
        var untuned = new OptimizedProgram<SentimentProgram>(
            new SentimentProgram(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var bound = untuned.Bind(context);
        var baseline = await MeasureAsync(bound, data, ct).ConfigureAwait(false);

        // MiproV2 scores every trial by running the program; the seed makes the search repeatable.
        var optimizer = new MiproV2<SentimentProgram>(
            RunOneAsync,
            proposer,
            candidatesPerSubmodule: InstructionCandidates,
            maxBootstrappedDemos: BootstrappedDemos,
            numTrials: NumTrials,
            numDemoSets: NumDemoSets,
            minibatchSize: MinibatchSize,
            fullEvalEvery: FullEvalEvery,
            seed: OptimizerSeed);
        var tuned = await optimizer.CompileAsync(bound, trainset, metric, context, ct: ct).ConfigureAwait(false);

        await persistence.SaveAsync(tuned, programPath, ct).ConfigureAwait(false);
        var loaded = await persistence.LoadAsync<SentimentProgram>(programPath, ct).ConfigureAwait(false);
        var reloaded = await MeasureAsync(loaded.Bind(context), data, ct).ConfigureAwait(false);

        var (markdown, json) = EvalComparisonReport.Render(
            "baseline", baseline, "tuned", reloaded, randomSeed: ReportSeed);

        return new PromptTuningResult(
            baseline,
            reloaded,
            baselineInstruction,
            loaded.InstructionOverrides.GetValueOrDefault(SentimentProgram.ClassifierName, baselineInstruction),
            loaded.DemosBySubmodule.TryGetValue(SentimentProgram.ClassifierName, out var demos) ? demos.Count : 0,
            programPath,
            markdown,
            json);
    }

    // One at a time so a scripted client answers in dataset order.
    Task<EvaluationResult<SentimentSig>> MeasureAsync(
        SentimentProgram program, IReadOnlyList<LabeledExample<SentimentSig>> data, CancellationToken ct) =>
        new Evaluate<SentimentSig>(program.ClassifyAsync, metric, maxDegreeOfParallelism: 1)
            .RunAsync(data, ct);

    internal static async Task<Example> RunOneAsync(
        SentimentProgram program, Example example, AmpelumContext context, CancellationToken ct)
    {
        var input = new SentimentSig
        {
            Feedback = Convert.ToString(
                example.Inputs[nameof(SentimentSig.Feedback)], CultureInfo.InvariantCulture) ?? "",
        };
        var prediction = await program.ClassifyAsync(input, ct).ConfigureAwait(false);
        // The optimizer harvests demos from the sink it puts on the context it hands the runner.
        context.TraceSink.Write(prediction.Trace);
        return Example.From(prediction.Value);
    }
}
