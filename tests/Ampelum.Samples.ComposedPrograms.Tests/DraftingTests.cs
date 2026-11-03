using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Fake;
using Shouldly;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The five-stage draft of one change: classify, branch, draft, tighten, score.</summary>
public sealed class DraftingTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Classifier_asks_for_xml_and_a_lowercase_kind_is_still_understood()
    {
        var model = DraftingRules.Create();

        var (note, _) = await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-103"), Ct);

        var classify = model.Calls.First();
        classify.User.ShouldStartWith("<");
        classify.User.ShouldContain("<Title>Remove legacy search API v1</Title>");
        note.Kind.ShouldBe(ChangeKind.Breaking);
    }

    [Fact]
    public void An_unknown_kind_is_an_error_not_a_default()
    {
        Should.Throw<InvalidOperationException>(() => KindParser.Parse("Chore"));
    }

    [Fact]
    public async Task Stages_run_in_order_and_each_hands_its_result_on()
    {
        var model = DraftingRules.Create();

        await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-103"), Ct);

        var calls = model.Calls;
        var classify = calls.ToList().FindIndex(c => c.Mentions("Classify one product change"));
        var draft = calls.ToList().FindIndex(c => c.Mentions(Helpers.DraftPhrase));
        var tighten = calls.ToList().FindIndex(c => c.Mentions("Shorten the release note"));
        classify.ShouldBe(0);
        draft.ShouldBeGreaterThan(classify);
        tighten.ShouldBeGreaterThan(draft);
        // The kind and impact the classifier chose reach the later stages.
        var draftFields = Wire.ChatFields(calls[draft].User);
        draftFields["Kind"].ShouldBe("breaking");
        draftFields["Impact"].ShouldBe("4");
        Wire.ChatFields(calls[tighten].User)["Kind"].ShouldBe("breaking");
    }

    [Fact]
    public async Task A_change_below_the_impact_bar_gets_one_chain_of_thought_call()
    {
        var model = DraftingRules.Create();

        var (note, route) = await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        route.Name.ShouldBe(NoteDrafter.PlainRoute);
        route.Strategy.ShouldBeNull();
        var draft = model.Drafts().ShouldHaveSingleItem();
        draft.Mentions("[[ ## reasoning ## ]]").ShouldBeTrue();
        note.Note.ShouldBe("Refactored autosave module.");
    }

    [Fact]
    public async Task A_change_at_the_impact_bar_takes_the_composed_route_for_its_kind()
    {
        var feature = await Rig.Drafter(DraftingRules.Create()).DraftAsync(await Helpers.RowAsync("C-101"), Ct);
        var fix = await Rig.Drafter(DraftingRules.Create()).DraftAsync(await Helpers.RowAsync("C-102"), Ct);
        var breaking = await Rig.Drafter(DraftingRules.Create()).DraftAsync(await Helpers.RowAsync("C-103"), Ct);

        feature.Route.Name.ShouldBe(NoteDrafter.ComposedRoute);
        feature.Route.Strategy.ShouldBe(DraftStrategy.MultiChainComparison);
        fix.Route.Strategy.ShouldBe(DraftStrategy.BestOfN);
        breaking.Route.Strategy.ShouldBe(DraftStrategy.Audiences);
    }

    [Fact]
    public async Task The_impact_bar_comes_from_the_options()
    {
        var high = Rig.Options(o => o with { ComposedImpact = 5 });
        var low = Rig.Options(o => o with { ComposedImpact = 1 });

        var feature = await Rig.Drafter(DraftingRules.Create(), high).DraftAsync(await Helpers.RowAsync("C-101"), Ct);
        var internalChange = await Rig.Drafter(DraftingRules.Create(), low).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        feature.Route.Name.ShouldBe(NoteDrafter.PlainRoute);
        internalChange.Route.Name.ShouldBe(NoteDrafter.ComposedRoute);
    }

    [Fact]
    public async Task The_tone_is_set_on_every_draft_call_by_the_config_wrapper()
    {
        var model = DraftingRules.Create();

        await Rig.Drafter(model).DraftAsync(await Helpers.RowAsync("C-102"), Ct);

        var drafts = model.Drafts().ToList();
        drafts.Count.ShouldBeGreaterThan(0);
        drafts.ShouldAllBe(c => Wire.ChatFields(c.User)["Tone"] == "plain, past tense, no marketing words");
    }

    [Fact]
    public async Task Without_a_tone_the_scripted_writer_copies_the_title_so_the_wrapper_is_what_makes_the_difference()
    {
        var options = Rig.Options(o => o with { Tone = "" });

        var (note, _) = await Rig.Drafter(DraftingRules.Create(), options).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        note.Note.ShouldBe("Refactor autosave module.");
    }

    [Fact]
    public async Task The_final_note_is_scored_with_the_quality_metric_so_a_vague_note_scores_lower_than_a_specific_one()
    {
        var vague = new ScriptedModel()
            .When("vague", call => call.Mentions(Helpers.DraftPhrase) ? Wire.Chat(("reasoning", "x"), ("Note", "Improved the product.")) : null)
            .Include(DraftingRules.Create());

        var (good, _) = await Rig.Drafter(DraftingRules.Create()).DraftAsync(await Helpers.RowAsync("C-106"), Ct);
        var (poor, _) = await Rig.Drafter(vague).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        good.Quality.ShouldBe(1.0);
        good.Words.ShouldBe(3);
        // None of the key words of "Refactor autosave module" appear in the vague note.
        poor.Note.ShouldBe("Improved the product.");
        poor.Quality.ShouldBe(0.0);
    }

    [Fact]
    public async Task A_note_over_the_word_limit_is_scored_down_in_proportion()
    {
        var longNote = string.Join(' ', Enumerable.Range(1, 24).Select(i => i == 1 ? "Refactored" : i == 2 ? "autosave" : i == 3 ? "module" : $"w{i}")) + ".";
        var stuck = new ScriptedModel()
            .When("long", call => call.Mentions(Helpers.DraftPhrase) ? Wire.Chat(("reasoning", "x"), ("Note", longNote)) : null)
            .When("keep", call => call.Mentions("Shorten the release note") ? Wire.Chat(("Note", longNote)) : null)
            .Include(DraftingRules.Create());

        var (note, _) = await Rig.Drafter(stuck).DraftAsync(await Helpers.RowAsync("C-106"), Ct);

        // Twelve allowed words out of twenty four written.
        note.Quality.ShouldBe(0.5, 0.001);
    }
}
