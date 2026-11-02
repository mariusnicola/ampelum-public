using SignalGrid.Ampelum;

namespace Company.WebApi.Signatures;

/// <summary>Single-turn QA signature: answer a user question concisely.</summary>
public sealed class QASignature : ISignature
{
    [Input]
    public string Question { get; set; } = string.Empty;

    [Output]
    public string Answer { get; set; } = string.Empty;
}
