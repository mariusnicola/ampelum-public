using System.Globalization;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Metrics;
using OptimizerMetric = SignalGrid.Ampelum.Optimizers.IMetric;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>
/// Scores 1 when the predicted label equals the expected label, ignoring case and surrounding
/// whitespace, else 0. Serves both the typed evaluator and the untyped optimizer.
/// </summary>
public sealed class LabelMatch : IMetric<SentimentSig>, OptimizerMetric
{
    /// <inheritdoc />
    public double Score(SentimentSig actual, SentimentSig expected, SignatureMetadata signature) =>
        Compare(actual.Sentiment, expected.Sentiment);

    /// <summary>Untyped entry point used by optimizers, which pass output dictionaries.</summary>
    public double Score(object actual, object expected, SignatureMetadata signature) =>
        Compare(Read(actual), Read(expected));

    static double Compare(string? actual, string? expected) =>
        string.Equals(actual?.Trim(), expected?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

    static string? Read(object value) => value switch
    {
        SentimentSig sig => sig.Sentiment,
        IReadOnlyDictionary<string, object?> outputs =>
            outputs.TryGetValue(nameof(SentimentSig.Sentiment), out var label)
                ? Convert.ToString(label, CultureInfo.InvariantCulture)
                : null,
        _ => throw new ArgumentException($"Cannot read a sentiment from {value.GetType().Name}.", nameof(value)),
    };
}
