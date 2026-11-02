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
/// <param name="ProgramPath">Where the tuned program was saved.</param>
/// <param name="ReportMarkdown">A/B comparison as markdown.</param>
/// <param name="ReportJson">A/B comparison as JSON.</param>
public sealed record PromptTuningResult(
    EvaluationResult<SentimentSig> Baseline,
    EvaluationResult<SentimentSig> Tuned,
    string BaselineInstruction,
    string TunedInstruction,
    string ProgramPath,
    string ReportMarkdown,
    string ReportJson);

/// <summary>Measures a prompt on labeled data, tunes it, saves it, reloads it and measures again.</summary>
public sealed class PromptTuningPipeline(
    IChatClient chat,
    string datasetPath,
    string programPath,
    IProgramPersistence persistence,
    LabelMatch metric)
{
    // The scripted fake client replays exactly this call sequence, so these stay at one.
    const int Rounds = 1;
    const int CandidatesPerSubmodule = 1;
    // Demos would add training rows to the prompt; this sample tunes the instruction only.
    const int BootstrappedDemos = 0;
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

        // MiproV2 scores candidates through the runner; BootstrapFewShot alone would ignore the metric.
        var optimizer = new MiproV2<SentimentProgram>(
            RunOneAsync, chat, Rounds, CandidatesPerSubmodule, BootstrappedDemos);
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
            programPath,
            markdown,
            json);
    }

    // One at a time so a scripted client answers in dataset order.
    Task<EvaluationResult<SentimentSig>> MeasureAsync(
        SentimentProgram program, IReadOnlyList<LabeledExample<SentimentSig>> data, CancellationToken ct) =>
        new Evaluate<SentimentSig>(program.ClassifyAsync, metric, maxDegreeOfParallelism: 1)
            .RunAsync(data, ct);

    static async Task<Example> RunOneAsync(
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
