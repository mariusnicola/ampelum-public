namespace SignalGrid.Ampelum.Context.Selection;

/// <summary>Request to produce a SelectionPlan for a single turn.</summary>
public sealed record SelectionRequest(
    string Mode,
    string UserText,
    System.Collections.Generic.IReadOnlySet<string> PinnedKeys,
    System.Collections.Generic.IReadOnlySet<string> DroppedKeys,
    SignalGrid.Ampelum.Context.Chapters.IChapterIndex? ChapterIndex,
    SignalGrid.Ampelum.Retrieval.IRetriever? Retriever);
