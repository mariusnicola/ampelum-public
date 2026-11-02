namespace SignalGrid.Ampelum.Context.Chapters;

/// <summary>Consolidates small chapters into meta-chapters using adjacency-based grouping.</summary>
public sealed class MetaChapterConsolidator
{
    private readonly int _minChapterEvents;

    /// <summary>Creates a consolidator. Chapters with event count below minChapterEvents are candidates.</summary>
    public MetaChapterConsolidator(int minChapterEvents = 5) => _minChapterEvents = minChapterEvents;

    /// <summary>Consolidates chapters into meta-chapters. Returns the consolidated list.</summary>
    public IReadOnlyList<Chapter> Consolidate(IReadOnlyList<Chapter> chapters, IChapterIndex index)
    {
        if (chapters.Count < 2) return chapters;

        var result = new List<Chapter>(chapters);
        var smallOnes = chapters.Where(c => c.EventCount < _minChapterEvents && !c.IsMetaChapter).ToList();
        if (smallOnes.Count < 2) return chapters;

        // Group adjacent small chapters
        var groups = new List<List<Chapter>>();
        var current = new List<Chapter>();
        foreach (var ch in smallOnes)
        {
            if (current.Count > 0 && ch.StartTurn > current.Last().EndTurn + 3)
            {
                groups.Add(current);
                current = [];
            }
            current.Add(ch);
        }
        if (current.Count >= 2) groups.Add(current);

        // Replace each group with a meta-chapter
        foreach (var group in groups)
        {
            var meta = new Chapter(
                $"meta-{group[0].ChapterId}",
                $"Meta: {string.Join(", ", group.Select(g => g.Title))}",
                group.Min(g => g.StartTurn),
                group.Max(g => g.EndTurn),
                group.Sum(g => g.EventCount),
                ChapterCloseReason.ModeTransition,
                group[0].EmbedderVersion,
                group.Sum(g => g.EstimatedTokens),
                0,
                true,
                group.Select(g => g.ChapterId).ToList());

            foreach (var ch in group) result.Remove(ch);
            result.Add(meta);
        }

        return result;
    }
}