using SignalGrid.Ampelum;

namespace Ampelum.Samples.PromptTuning;

/// <summary>Typed signature for the customer feedback sentiment task.</summary>
[Signature("Classify the sentiment of the customer feedback.")]
public sealed record SentimentSig : ISignature
{
    /// <summary>The customer's feedback text.</summary>
    [Input(Desc = "Customer feedback text")]
    public string Feedback { get; init; } = "";

    /// <summary>The predicted or labeled sentiment.</summary>
    [Output(Desc = "One of: positive, negative, neutral")]
    public string Sentiment { get; init; } = "";
}
