using SignalGrid.Ampelum;

namespace Ampelum.IntegrationTests.Fixtures;

/// <summary>
/// Shared signatures for the Pareto integration suite. Kept tiny on purpose so the
/// real-LM hops stay well under the per-test cost cap (≤5 calls × ≤50 tokens).
/// </summary>
[Signature("Answer the question with a single short factoid (one or two words).")]
public sealed record QaSig : ISignature
{
    /// <summary>The user's question.</summary>
    [Input] public required string Question { get; init; }

    /// <summary>The factoid answer.</summary>
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Signature used for ReAct-driven lookup scenarios.</summary>
[Signature("Use the available tools to answer the question. End your final turn with `final_answer: <answer>`.")]
public sealed record LookupSig : ISignature
{
    /// <summary>The user's question.</summary>
    [Input] public required string Question { get; init; }

    /// <summary>The looked-up answer.</summary>
    [Output] public string Answer { get; init; } = "";
}
