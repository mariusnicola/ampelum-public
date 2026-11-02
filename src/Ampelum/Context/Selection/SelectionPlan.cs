namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Plan for what to include in the context for a single turn.</summary>
public sealed record SelectionPlan(
    ContextPrefix Prefix,
    System.Collections.Generic.IReadOnlyList<SelectedEventInfo> SelectedEvents,
    System.Collections.Generic.IReadOnlyList<DecisionTraceEntry> DecisionTrace,
    int EstimatedTokens,
    string Mode);
