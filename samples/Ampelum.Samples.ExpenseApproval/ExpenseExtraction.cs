using SignalGrid.Ampelum;

namespace Ampelum.Samples.ExpenseApproval;

/// <summary>Signature that turns a free-text expense note into structured fields.</summary>
[Signature("Extract the expense from the note. Amount is the number exactly as written, without currency symbol.")]
public sealed record ExpenseExtraction : ISignature
{
    [Input] public required string Text { get; init; }
    [Output] public string Vendor { get; init; } = "";
    [Output] public string Amount { get; init; } = "";
    [Output] public string Category { get; init; } = "";
}
