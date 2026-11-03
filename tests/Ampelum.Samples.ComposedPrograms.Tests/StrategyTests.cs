using Ampelum.Samples.ComposedPrograms.Fake;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The four ways a change that deserves effort is drafted.</summary>
public sealed class StrategyTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BestOfN_asks_n_times_and_keeps_the_note_the_quality_metric_likes()
    {
        var model = DraftingRules.Create();

        var (note, _) = await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-102"), Ct);

        model.Drafts().Count().ShouldBe(3);
        model.Drafts().Select(c => c.User).Distinct().Count().ShouldBe(1);
        note.Note.ShouldBe("Fixed duplicate charge on retry.");
    }

    [Fact]
    public async Task The_candidate_count_comes_from_the_options()
    {
        var model = DraftingRules.Create();
        var options = Rig.Options(o => o with { Candidates = 2 });

        await Rig.Drafter(model, options).DraftAsync(await Helpers.RowAsync("C-102"), Ct);

        model.Drafts().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Refine_retries_with_feedback_until_the_score_passes_the_threshold()
    {
        var model = DraftingRules.Create();
        var options = Rig.Options(o => o with { ComposedImpact = 1 });

        var (note, route) = await Rig.Drafter(model, options).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        route.Strategy.ShouldBe(DraftStrategy.Refine);
        var drafts = model.Drafts().ToList();
        drafts.Count.ShouldBe(2);
        drafts[0].All.ShouldNotContain("The previous answer scored");
        drafts[1].All.ShouldContain("The previous answer scored");
        drafts[1].All.ShouldContain("mention");
        note.Note.ShouldBe("Refactored autosave module.");
    }

    [Fact]
    public async Task Refine_stops_at_the_refinement_limit_when_the_score_never_passes()
    {
        var model = DraftingRules.Create();
        var options = Rig.Options(o => o with { ComposedImpact = 1, RefineThreshold = 2.0, MaxRefinements = 2 });

        await Rig.Drafter(model, options).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        model.Drafts().Count().ShouldBe(3);
    }

    [Fact]
    public async Task MultiChainComparison_asks_for_candidates_then_one_synthesis_that_sees_them()
    {
        var model = DraftingRules.Create();

        var (note, _) = await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-101"), Ct);

        model.Count("Synthesise a single best answer").ShouldBe(1);
        var synthesis = model.Calls.Single(c => c.Mentions("Synthesise a single best answer"));
        // Three candidates were written first, and the weak one is among those the synthesis was shown.
        synthesis.All.ShouldContain("Improved the product.");
        model.Drafts().Count().ShouldBe(4);
        note.Note.ShouldBe("Added bulk invoice export.");
    }

    [Fact]
    public async Task Parallel_writes_one_note_per_audience_in_one_call_each_and_joins_them()
    {
        var model = DraftingRules.Create();

        await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-103"), Ct);

        var audiences = model.Drafts().Select(c => c.Options?.Instructions).ToList();
        audiences.Count.ShouldBe(3);
        audiences.ShouldAllBe(a => a != null && a.StartsWith("Audience:"));
        audiences.Distinct().Count().ShouldBe(3);
        var tighten = model.Calls.First(c => c.Mentions("Shorten the release note"));
        Wire.ChatFields(tighten.User)["Draft"].Split(" | ").Length.ShouldBe(3);
    }

    [Fact]
    public async Task The_audiences_come_from_the_options()
    {
        var model = DraftingRules.Create();
        var options = Rig.Options(o => o with { Audiences = ["Audience: developers who call the API."] });

        await Rig.Drafter(model, options).DraftAsync(await Helpers.RowAsync("C-103"), Ct);

        model.Drafts().Count().ShouldBe(1);
    }
}
