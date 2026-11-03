using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Fake;
using Shouldly;
using SignalGrid.Ampelum.Composition;
using SignalGrid.Ampelum.Orchestration;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>The tightening loop and the stop rule built from registered predicates.</summary>
public sealed class TightenTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Words(int count) => string.Join(' ', Enumerable.Range(1, count).Select(i => $"word{i}"));

    static TightenSig Start(int words) =>
        new() { Title = "t", Draft = Words(words), Kind = "Fix", Impact = 2, MaxWords = 12 };

    /// <summary>A writer that never shortens anything.</summary>
    static ScriptedModel Stuck() => new ScriptedModel()
        .When("stuck", call => call.Mentions("Shorten the release note")
            ? Wire.Chat(("Note", Wire.ChatFields(call.User)["Draft"]))
            : null);

    static TightenStage Stage(ScriptedModel model) => new(Rig.Context(model), Rig.Options());

    [Fact]
    public async Task A_note_that_fits_stops_the_loop_before_any_model_call()
    {
        var model = DraftingRules.Create();

        var (result, runs) = await Stage(model).RunAsync(Start(10), ct: Ct);

        runs.ShouldBe(0);
        model.Calls.ShouldBeEmpty();
        result.FinalState.Words.ShouldBe(10);
    }

    [Fact]
    public async Task A_long_note_is_shortened_each_pass_until_it_fits()
    {
        var model = DraftingRules.Create();

        var (result, runs) = await Stage(model).RunAsync(Start(20), ct: Ct);

        runs.ShouldBe(2);
        model.Count("Shorten the release note").ShouldBe(2);
        result.FinalState.Words.ShouldBe(11);
        // Each pass starts from the previous result, not from the original draft.
        var drafts = model.Calls.Select(c => Wire.ChatFields(c.User)["Draft"].Split(' ').Length).ToList();
        drafts.ShouldBe([20, 15]);
    }

    [Fact]
    public async Task A_note_that_stops_shrinking_is_accepted_when_it_is_close_enough()
    {
        var model = Stuck();

        var (result, runs) = await Stage(model).RunAsync(Start(15), ct: Ct);

        runs.ShouldBe(2);
        result.FinalState.Words.ShouldBe(15);
    }

    [Fact]
    public async Task A_note_that_stops_shrinking_far_above_the_limit_runs_into_the_pass_limit()
    {
        var model = Stuck();

        var (result, runs) = await Stage(model).RunAsync(Start(30), ct: Ct);

        runs.ShouldBe(3);
        result.FinalState.Words.ShouldBe(30);
    }

    [Fact]
    public async Task The_pass_limit_comes_from_the_options()
    {
        var model = Stuck();
        var stage = new TightenStage(Rig.Context(model), Rig.Options(o => o with { TightenPasses = 1 }));

        var (_, runs) = await stage.RunAsync(Start(30), ct: Ct);

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_caller_can_replace_the_stop_rule_with_a_shipped_predicate()
    {
        var model = Stuck();

        var (_, runs) = await Stage(model).RunAsync(Start(30), new MaxIterations<TightenSig>(1), Ct);

        runs.ShouldBe(1);
    }

    [Fact]
    public void The_stop_rule_is_any_of_fits_or_stalled_and_close_or_limit()
    {
        Stage(Stuck()).StopRule().ShouldBeOfType<CompositeAny<TightenSig>>();
    }

    [Fact]
    public void Registered_names_build_the_shipped_predicate_classes()
    {
        // Explicit, so the test does not depend on which other class ran first.
        TightenStage.RegisterPredicates();
        var fits = LoopPredicateRegistry.Create<TightenSig>(TightenStage.FitsRule, new Dictionary<string, object?>
        {
            ["field"] = "Words",
            ["threshold"] = 12m,
        });
        var stalled = LoopPredicateRegistry.Create<TightenSig>(TightenStage.StalledRule, new Dictionary<string, object?>
        {
            ["field"] = "Words",
            ["epsilon"] = 1m,
        });
        var limit = LoopPredicateRegistry.Create<TightenSig>(TightenStage.LimitRule, new Dictionary<string, object?> { ["max"] = 3 });

        fits.ShouldBeOfType<FieldThreshold<TightenSig>>();
        stalled.ShouldBeOfType<FieldChangeBelow<TightenSig>>();
        limit.ShouldBeOfType<MaxIterations<TightenSig>>();
    }

    [Fact]
    public void Registering_twice_is_harmless_and_each_name_stays_available()
    {
        TightenStage.RegisterPredicates();
        TightenStage.RegisterPredicates();

        LoopPredicateRegistry.Names<TightenSig>().ShouldBe(
            [TightenStage.FitsRule, TightenStage.StalledRule, TightenStage.LimitRule], ignoreOrder: true);
    }

    [Fact]
    public async Task The_stalled_epsilon_comes_from_the_options()
    {
        var stage = new TightenStage(Rig.Context(Stuck()), Rig.Options(o => o with { StalledEpsilon = 0m }));

        var (_, runs) = await stage.RunAsync(Start(15), ct: Ct);

        // With no tolerance an unchanged count is not stalled, so only the pass limit stops the loop.
        runs.ShouldBe(3);
    }

    [Fact]
    public async Task The_loop_reports_why_it_stopped_with_the_predicate_reason()
    {
        var (result, _) = await Stage(DraftingRules.Create()).RunAsync(Start(20), ct: Ct);

        result.Reason.ShouldBe("stopped after 2 body runs");
    }
}
