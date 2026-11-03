using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Tuning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Retrieval.Evaluation;

namespace Ampelum.Samples.ComposedPrograms;

public sealed partial class ReleaseNotesPipeline
{
    /// <summary>Drafts one change on its own, with the style notes of the options.</summary>
    public async Task<(DraftedNote Note, DraftRoute Route)> DraftOneAsync(ChangeRow row, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        using var ambient = AmpelumContext.Push(ctx);
        var style = await _style.Value.ConfigureAwait(false);
        return await new NoteDrafter(ctx, _options, style, _loggers.CreateLogger<NoteDrafter>())
            .DraftAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>The style notes that would go into the draft of this change.</summary>
    public async Task<string> StyleNotesAsync(ChangeRow row, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        using var ambient = AmpelumContext.Push(scope.ServiceProvider.GetRequiredService<AmpelumContext>());
        var style = await _style.Value.ConfigureAwait(false);
        return await style.NotesForAsync(row.Title, row.Details, ct).ConfigureAwait(false);
    }

    /// <summary>Mean rank metrics of the style retriever over the labeled queries in the data folder.</summary>
    public async Task<RetrievalEvalResult> EvaluateRetrievalAsync(int topK, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        using var ambient = AmpelumContext.Push(scope.ServiceProvider.GetRequiredService<AmpelumContext>());
        var style = await _style.Value.ConfigureAwait(false);
        return await style.EvaluateAsync(_source.PathOf("retrieval-eval.csv"), topK, ct).ConfigureAwait(false);
    }

    /// <summary>The note a nearest-neighbour tuned drafter writes, and the approved notes it was shown.</summary>
    public async Task<(string Note, IReadOnlyList<Example> Neighbours)> DraftWithNeighboursAsync(
        ChangeRow row, DraftedNote classified, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var approved = await _source.LoadApprovedAsync(ct).ConfigureAwait(false);
        var knn = await Tuning(ctx).KnnAsync(PromptTuning.ToExamples(approved), ct).ConfigureAwait(false);
        var (prediction, neighbours) = await knn.DraftAsync(DraftInput(row, classified), ct).ConfigureAwait(false);
        return (prediction.Value.Note, neighbours);
    }

    /// <summary>Two copies of the plain drafter answer and the reducer keeps the better note.</summary>
    public async Task<(string Note, int Members)> DraftWithEnsembleAsync(
        ChangeRow row, DraftedNote classified, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var tuning = Tuning(ctx);
        var ensemble = await tuning.EnsembleAsync([tuning.Baseline(), tuning.Baseline()], ct).ConfigureAwait(false);
        return (await ensemble.DraftAsync(DraftInput(row, classified), ct).ConfigureAwait(false), ensemble.Members);
    }

    /// <summary>The change as the drafter receives it, with the kind and impact the classifier chose for it.</summary>
    DraftSig DraftInput(ChangeRow row, DraftedNote classified) => new()
    {
        Title = row.Title,
        Details = row.Details,
        Kind = classified.Kind.ToString(),
        Impact = classified.Impact,
        Tone = _options.Tone,
    };
}
