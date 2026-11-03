using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Review;
using Ampelum.Samples.ComposedPrograms.Style;
using Shouldly;
using SignalGrid.Ampelum.Agents;
using SignalGrid.Ampelum.Retrieval;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The three memory tiers of the review panel.</summary>
public sealed class MemoryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_vector_tier_finds_an_old_remark_by_meaning_after_the_recent_tier_dropped_it()
    {
        // Capacity one, and the summary tier folds older remarks into a summary: only the vector tier still has the text.
        var options = Rig.Options(o => o with { PanelMemoryCapacity = 1, EpisodicThreshold = 2, EpisodicWindow = 1 });
        var memory = ReviewPanel.BuildMemory(ReleaseScript.Create(), new InMemoryVectorStore(), new HashingEmbeddingGenerator(), options);
        await memory.AppendAsync(new AgentMessage("Edith", "editor", "The login redirect loop note is clear."), Ct);
        await memory.AppendAsync(new AgentMessage("Carl", "compliance", "Invoice totals need a migration hint."), Ct);
        await memory.AppendAsync(new AgentMessage("Pia", "publisher", "Dashboard speed note is fine."), Ct);

        var recent = await memory.GetRecentAsync(5, Ct);
        var found = await memory.QueryAsync("login redirect loop", 3, Ct);

        recent.Select(m => m.Content).ShouldNotContain(c => c.Contains("login redirect loop"));
        found.Select(m => m.Content).ShouldContain(c => c.Contains("login redirect loop"));
    }
}
