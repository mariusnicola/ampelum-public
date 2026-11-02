using SignalGrid.Ampelum;

namespace Ampelum.Samples.AgentService;

/// <summary>The program the Host serves: one question in, one answer out.</summary>
[Signature("Answer the customer's question in one or two sentences.")]
public sealed record AnswerSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Tool agent: a ReAct loop over tools that come from an MCP tool source, so one output field.</summary>
[Signature("Report the status of the order the question refers to. Use the lookup_order tool.")]
public sealed record OrderStatusSignature : ISignature
{
    [Input] public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}
