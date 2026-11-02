namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Information about a single event selected for inclusion in context.</summary>
public sealed record SelectedEventInfo(
    string EventKey,
    string EventType,
    string Body,
    int TokenCount,
    string Source);
