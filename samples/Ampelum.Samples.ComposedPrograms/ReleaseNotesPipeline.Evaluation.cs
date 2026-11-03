using Ampelum.Samples.ComposedPrograms.Evaluation;
using Ampelum.Samples.ComposedPrograms.Tuning;
using Microsoft.Extensions.DependencyInjection;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Metrics;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.ComposedPrograms;

/// <summary>Result of tuning the drafting call and measuring it on the approved notes.</summary>
public sealed record TuningResult(double BaselineRougeL, double TunedRougeL, OptimizedProgram<DraftProgram> Program);

public sealed partial class ReleaseNotesPipeline
{
    ReleaseEvaluation Evaluation => new(_services.GetRequiredService<IMetricRegistry>(), _source);

    /// <summary>Scores the classifier on the approved notes, with the metrics named in the options.</summary>
    public async Task<EvaluationSummary> EvaluateClassificationAsync(CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var classifier = new Predict<ClassifySig>(ctx with { Adapter = new XMLAdapter(XMLAdapter.ParseMode.Strict) });
        return await Evaluation.ClassifyAsync((sig, token) => classifier.ForwardAsync(sig, ct: token), _options.ClassifyMetrics, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Scores a plain drafting call, which gets no tone and no style notes, on the approved notes.</summary>
    public async Task<EvaluationSummary> EvaluateDraftingAsync(CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var drafter = new Predict<DraftSig>(ctx);
        return await Evaluation.DraftAsync((sig, token) => drafter.ForwardAsync(sig, ct: token), _options.DraftMetrics, ct)
            .ConfigureAwait(false);
    }

    /// <summary>The tuning helpers, bound to a fresh scope of this pipeline.</summary>
    public PromptTuning Tuning(AmpelumContext ctx) => new(
        ctx, _chat, _services.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(),
        _options);

    /// <summary>Runs GEPA on the approved notes and measures the baseline and the winner with ROUGE-L.</summary>
    public async Task<TuningResult> TuneAsync(GepaOptions gepa, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var tuning = Tuning(ctx);
        var approved = await _source.LoadApprovedAsync(ct).ConfigureAwait(false);

        var tuned = await tuning.GepaAsync(PromptTuning.ToExamples(approved), gepa, ct).ConfigureAwait(false);
        var baseline = await MeanRougeLAsync(tuning.Baseline(), ct).ConfigureAwait(false);
        var winner = await MeanRougeLAsync(tuned.Program, ct).ConfigureAwait(false);
        return new TuningResult(baseline, winner, tuned);
    }

    async Task<double> MeanRougeLAsync(DraftProgram program, CancellationToken ct)
    {
        var summary = await Evaluation.DraftAsync(program.ForwardAsync, [MetricNames.NoteRougeL], ct).ConfigureAwait(false);
        return summary.Mean(MetricNames.NoteRougeL);
    }
}
