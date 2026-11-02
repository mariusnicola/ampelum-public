namespace SignalGrid.Ampelum.Context;

/// <summary>A decision extracted from a chapter summary, grounded in specific source events.</summary>
public sealed record GroundedDecision(
    string Statement,
    IReadOnlyList<string> SourceEventKeys);