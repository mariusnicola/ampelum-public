using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.PromptTuning;

/// <summary>
/// Program wrapper around one classifier. It implements <see cref="IComposedModule"/> because
/// only named submodules receive instruction overrides when an optimized program is bound.
/// </summary>
public sealed class SentimentProgram : IComposedModule
{
    /// <summary>Submodule name under which the tuned instruction is stored.</summary>
    public const string ClassifierName = "classify";

    /// <summary>Creates an unbound program; <c>OptimizedProgram.Bind</c> supplies the real context.</summary>
    public SentimentProgram() : this(Predict<SentimentSig>.FromAmbient())
    {
    }

    /// <summary>Creates a program around an existing classifier.</summary>
    public SentimentProgram(Predict<SentimentSig> classifier) => Classifier = classifier;

    /// <summary>The single classifier submodule.</summary>
    public Predict<SentimentSig> Classifier { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> Submodules =>
        new Dictionary<string, object> { [ClassifierName] = Classifier };

    /// <inheritdoc />
    public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) =>
        new SentimentProgram((Predict<SentimentSig>)newSubmodules[ClassifierName]);

    /// <summary>Classifies one feedback text.</summary>
    public Task<Prediction<SentimentSig>> ClassifyAsync(SentimentSig input, CancellationToken ct = default) =>
        Classifier.ForwardAsync(input, ct: ct);
}
