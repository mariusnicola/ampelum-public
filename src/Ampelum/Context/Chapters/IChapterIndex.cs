namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Index of all chapters in the current session, with embedding cache support.</summary>
public interface IChapterIndex
{
    /// <summary>Adds a chapter entry to the index.</summary>
    ValueTask AddAsync(ChapterIndexEntry entry, CancellationToken ct = default);
    /// <summary>Updates an existing chapter entry.</summary>
    ValueTask UpdateAsync(string chapterId, ChapterIndexEntry updated, CancellationToken ct = default);
    /// <summary>Gets a chapter entry by ID, or null if not found.</summary>
    ValueTask<ChapterIndexEntry?> GetAsync(string chapterId, CancellationToken ct = default);
    /// <summary>Gets all chapter entries.</summary>
    ValueTask<IReadOnlyList<ChapterIndexEntry>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Total estimated tokens across all chapters.</summary>
    ValueTask<int> EstimatedTokensAsync(CancellationToken ct = default);
    /// <summary>Rebuilds the index from the event store.</summary>
    ValueTask RebuildFromStoreAsync(IEventStore store, CancellationToken ct = default);
}
