using SignalGrid.Ampelum;

namespace Ampelum.Samples.SafetyGateway;

/// <summary>Plain question step: one call, no tools, so it can be replayed from the audit file.</summary>
[Signature("Answer the employee's question about company policy. Be brief and factual.")]
public sealed record PolicyAnswerSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Tool step: a ReAct loop, so exactly one output field.</summary>
[Signature("Answer the employee's question. Use the tools when a lookup is needed. Pass credential aliases to tools and never repeat a credential.")]
public sealed record ToolAnswerSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}
