namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Record representing a single chapter - a coherent unit of work with auto-detected boundaries.</summary>
public sealed record Chapter(
    string ChapterId,
    string Title,
    int StartTurn,
    int EndTurn,
    int EventCount,
    ChapterCloseReason Reason,
    string EmbedderVersion,
    int EstimatedTokens,
    int SummarisationAttempts,
    bool IsMetaChapter,
    IReadOnlyList<string> ChildChapterIds);
