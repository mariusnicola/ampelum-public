namespace SignalGrid.Ampelum.Context;

/// <summary>Filter criteria for reading from an IEventStore.</summary>
public sealed record EventQuery(
    int? SinceTurn = null,
    int? UntilTurn = null,
    string[]? Types = null,
    string? ToolKeyPrefix = null,
    string? ContentHashPrefix = null,
    bool IncludeSuperseded = false);