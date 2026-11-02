using SignalGrid.Ampelum;

namespace Ampelum.Samples.BulkDocuments;

/// <summary>Interactive summary: one input, one output field, so the streamed field is the summary itself.</summary>
[Signature("Summarize the document in two sentences. Use only what the document says.")]
public sealed record SummarySignature : ISignature
{
    [Input] public required string Text { get; init; }
    [Output] public string Summary { get; init; } = "";
}
