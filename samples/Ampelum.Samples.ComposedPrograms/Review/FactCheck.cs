using SignalGrid.Ampelum;

namespace Ampelum.Samples.ComposedPrograms.Review;

/// <summary>Asks a model to compare a note with the change record it can read through the change_details tool.</summary>
public sealed class FactCheck(AmpelumContext ctx, IReadOnlyDictionary<string, ChangeRow> changes, int maxIterations = 3)
{
    public async Task<Prediction<FactCheckSig>> CheckAsync(DraftedNote note, CancellationToken ct = default)
    {
        IReadOnlyList<IReActTool> tools = [new ChangeDetailsTool(changes)];
        var react = new ReAct<FactCheckSig>(ctx, tools, maxIterations);
        return await react.ForwardAsync(new FactCheckSig { Note = note.Note, ChangeId = note.Id }, ct: ct).ConfigureAwait(false);
    }
}
