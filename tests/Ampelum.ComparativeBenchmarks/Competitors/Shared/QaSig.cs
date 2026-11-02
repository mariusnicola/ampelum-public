using SignalGrid.Ampelum;

namespace Ampelum.ComparativeBenchmarks.Competitors.Shared;

/// <summary>
/// Single-input single-output signature shared by every comparative scenario
/// so each competitor processes the same prompt shape.
/// </summary>
[Signature("Answer the question.")]
public sealed record QaSig : ISignature
{
    [Input]  public required string Question { get; init; }
    [Output] public string Answer { get; init; } = "";
}
