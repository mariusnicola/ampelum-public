namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Cached entry for a chapter in the index, used for retrieval and display.</summary>
public sealed record ChapterIndexEntry(
    string ChapterId,
    string Title,
    string Abstract,
    IReadOnlyList<string> KeyDecisions,
    int EstimatedTokens,
    string EmbedderVersion,
    byte[] AbstractEmbeddingBlob,
    int DeferredPinCount);
