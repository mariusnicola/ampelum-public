using SignalGrid.Ampelum;

namespace Company.Research.Signatures;

/// <summary>Summarise an article into one sentence.</summary>
public sealed class SummariseSignature : ISignature
{
    [Input]
    public string Article { get; set; } = string.Empty;

    [Output]
    public string Summary { get; set; } = string.Empty;
}
