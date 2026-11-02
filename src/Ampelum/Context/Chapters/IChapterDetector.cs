namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Detects when a chapter should be closed based on session state.</summary>
public interface IChapterDetector
{
    /// <summary>Returns the reason to close the current chapter, or null if no close needed.</summary>
    Task<ChapterCloseReason?> ShouldCloseAsync(IEventStore store, int currentTurn, CancellationToken ct);
}
