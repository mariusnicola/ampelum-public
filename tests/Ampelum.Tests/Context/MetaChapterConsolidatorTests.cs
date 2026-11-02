using SignalGrid.Ampelum.Context;
using SignalGrid.Ampelum.Context.Chapters;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Context;

public class MetaChapterConsolidatorTests
{
    // Stub chapter index for tests — IChapterIndex isn't needed by Consolidate
    private sealed class StubChapterIndex : IChapterIndex
    {
        public ValueTask AddAsync(ChapterIndexEntry entry, CancellationToken ct = default) => default;
        public ValueTask UpdateAsync(string chapterId, ChapterIndexEntry updated, CancellationToken ct = default) => default;
        public ValueTask<ChapterIndexEntry?> GetAsync(string chapterId, CancellationToken ct = default) => default;
        public ValueTask<IReadOnlyList<ChapterIndexEntry>> GetAllAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<ChapterIndexEntry>>([]);
        public ValueTask<int> EstimatedTokensAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RebuildFromStoreAsync(IEventStore store, CancellationToken ct = default) => default;
    }

    [Fact]
    public void Consolidate_TwoSmall_CombinesIntoMeta()
    {
        var chapters = new List<Chapter>
        {
            new("ch1", "Alpha", 1, 2, 3, ChapterCloseReason.UserTurnLimit, "v1", 100, 0, false, []),
            new("ch2", "Beta", 3, 4, 4, ChapterCloseReason.UserTurnLimit, "v1", 150, 0, false, []),
        };
        var idx = new StubChapterIndex();
        var c = new MetaChapterConsolidator(5);
        var result = c.Consolidate(chapters, idx);
        result.Count.ShouldBe(1);
        result[0].IsMetaChapter.ShouldBeTrue();
        result[0].ChildChapterIds.ShouldContain("ch1");
        result[0].ChildChapterIds.ShouldContain("ch2");
    }

    [Fact]
    public void Consolidate_NoSmall_ReturnsSame()
    {
        var chapters = new List<Chapter>
        {
            new("ch1", "Alpha", 1, 2, 10, ChapterCloseReason.UserTurnLimit, "v1", 100, 0, false, []),
        };
        var idx = new StubChapterIndex();
        var c = new MetaChapterConsolidator(5);
        var result = c.Consolidate(chapters, idx);
        result.Count.ShouldBe(1);
        result[0].IsMetaChapter.ShouldBeFalse();
    }
}
