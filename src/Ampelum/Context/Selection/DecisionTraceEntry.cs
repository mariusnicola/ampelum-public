namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Trace entry explaining why a specific event was kept, aged, dropped, or pinned.</summary>
public sealed record DecisionTraceEntry(
    string EventKey,
    string Reason,
    string Action,
    int TokenCount);
