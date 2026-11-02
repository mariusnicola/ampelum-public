namespace SignalGrid.Ampelum.Context.Cost;

/// <summary>Cost breakdown for a single turn.</summary>
public sealed record PerTurnCost(
    int Turn,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    decimal InputCost,
    decimal OutputCost,
    decimal CacheReadCost,
    decimal CacheWriteCost,
    decimal TotalCost);