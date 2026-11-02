namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Stable cache prefix that precedes all selected events in context.</summary>
public sealed record ContextPrefix(
    string ModeBanner,
    string GoalAnchor,
    string ChapterIndex);
