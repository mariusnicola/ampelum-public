namespace Ampelum.Tests.Composition._Sigs;

using SignalGrid.Ampelum;

/// <summary>Minimal signature used across composition test fixtures.</summary>
[Signature("Echo the input as an answer.")]
public sealed record TestSig : ISignature
{
    [Input] public string Question { get; init; } = "";
    [Output] public string Answer { get; init; } = "";
}

/// <summary>Numeric-output signature used by FieldThreshold/FieldChangeBelow tests.</summary>
[Signature("Score the question.")]
public sealed record ScoreSig : ISignature
{
    [Input] public string Question { get; init; } = "";
    [Output] public decimal Score { get; init; }
}
