using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using OptimizerMetric = SignalGrid.Ampelum.Optimizers.IMetric;

namespace Ampelum.Samples.ComposedPrograms.Tuning;

/// <summary>
/// Scores a drafted note against the approved one with the shipped ROUGE-L metric. It serves both the
/// typed evaluator and the untyped optimizers, which hand over output dictionaries.
/// </summary>
public sealed class NoteMatch : IMetric<DraftSig>, OptimizerMetric
{
    readonly RougeL<DraftSig> _rouge = new(nameof(DraftSig.Note));

    public double Score(DraftSig actual, DraftSig expected, SignatureMetadata signature) =>
        _rouge.Score(actual, expected, signature);

    public double Score(object actual, object expected, SignatureMetadata signature) =>
        _rouge.Score(AsSignature(actual), AsSignature(expected), signature);

    static DraftSig AsSignature(object value) => value switch
    {
        DraftSig sig => sig,
        IReadOnlyDictionary<string, object?> outputs => new DraftSig
        {
            Title = "",
            Details = "",
            Note = Convert.ToString(outputs.GetValueOrDefault(nameof(DraftSig.Note)), System.Globalization.CultureInfo.InvariantCulture) ?? "",
        },
        _ => throw new ArgumentException($"Cannot read a note from {value.GetType().Name}.", nameof(value)),
    };
}
