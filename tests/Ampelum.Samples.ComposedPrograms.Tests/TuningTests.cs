using Ampelum.Samples.ComposedPrograms.Fake;
using Ampelum.Samples.ComposedPrograms.Tuning;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Samples.ComposedPrograms.Tests;

/// <summary>GEPA, nearest-neighbour few-shot and the ensemble, each driven through the real optimizer.</summary>
public sealed class TuningTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DraftSig Input(string title = "Fix duplicate charge on retry") => new()
    {
        Title = title,
        Details = "Retrying a failed card payment no longer creates a second charge.",
        Kind = "Fix",
        Impact = 2,
        Tone = "plain, past tense, no marketing words",
    };

    [Fact]
    public async Task Gepa_asks_the_proposer_and_the_winner_writes_the_note_the_baseline_does_not()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var result = await pipeline.TuneAsync(new GepaOptions(), Ct);

        model.Calls.ShouldContain(c => c.User.Contains("prompt engineer"));
        result.BaselineRougeL.ShouldBe(0.326, 0.001);
        result.TunedRougeL.ShouldBe(0.540, 0.001);
        result.TunedRougeL.ShouldBeGreaterThan(result.BaselineRougeL);
        result.Program.Score.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_tuned_program_sends_the_tuned_instruction_and_the_baseline_does_not()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        var tuned = (await pipeline.TuneAsync(new GepaOptions(), Ct)).Program.Program;
        var before = model.Calls.Count;

        var note = (await tuned.ForwardAsync(Input(), Ct)).Value.Note;

        model.Calls.Skip(before).Single().System.ShouldContain(DraftingRules.TunedInstruction);
        note.ShouldBe("Fixed duplicate charge on retry.");
    }

    [Fact]
    public async Task The_baseline_program_uses_the_signature_instruction()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        await using var scope = pipeline.Services.CreateAsyncScope();
        var baseline = pipeline.Tuning(scope.ServiceProvider.GetRequiredService<AmpelumContext>()).Baseline();

        await baseline.ForwardAsync(Input(), Ct);

        model.Calls.Single().System.ShouldNotContain(DraftingRules.TunedInstruction);
        model.Calls.Single().System.ShouldContain(Helpers.DraftPhrase);
    }

    [Fact]
    public async Task Nearest_neighbour_few_shot_shows_the_drafter_the_approved_notes_of_the_nearest_changes()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var (_, neighbours) = await pipeline.DraftWithNeighboursAsync(await Helpers.RowAsync("C-102"), await Helpers.NoteAsync("C-102"), Ct);

        neighbours.Count.ShouldBe(3);
        var titles = neighbours.Select(n => n.Inputs["Title"]?.ToString()).ToList();
        titles.ShouldBe(
            ["Fix login loop on expired session", "Fix wrong total on invoices", "Fix slow dashboard"], ignoreOrder: true);
        // The model received those examples in the prompt, not just the program.
        var fields = Wire.ChatFields(model.Drafts().Last().User);
        fields["Kind"].ShouldBe("Fix");
        fields["Impact"].ShouldBe("4");
        var sent = model.Drafts().Last().All;
        sent.ShouldContain("Fixed a login redirect loop after a session expired.");
        sent.ShouldContain("Fixed slow dashboard loading for large accounts.");
        sent.ShouldNotContain("Added team workspaces for shared projects.");
    }

    [Fact]
    public async Task The_drafter_is_given_the_kind_and_impact_of_the_change_it_drafts()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        await pipeline.DraftWithNeighboursAsync(await Helpers.RowAsync("C-103"), await Helpers.NoteAsync("C-103"), Ct);

        var fields = Wire.ChatFields(model.Drafts().Last().User);
        fields["Kind"].ShouldBe("Breaking");
        fields["Impact"].ShouldBe("4");
    }

    [Fact]
    public async Task The_neighbour_count_comes_from_the_options()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options(o => o with { KnnNeighbours = 1 }));

        var (_, neighbours) = await pipeline.DraftWithNeighboursAsync(await Helpers.RowAsync("C-102"), await Helpers.NoteAsync("C-102"), Ct);

        neighbours.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_ensemble_runs_every_member_and_the_reducer_keeps_the_best_note()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var (note, members) = await pipeline.DraftWithEnsembleAsync(await Helpers.RowAsync("C-102"), await Helpers.NoteAsync("C-102"), Ct);

        members.ShouldBe(2);
        model.Drafts().Count().ShouldBe(2);
        // The first member wrote the weak note and the second the good one; the reducer chose by quality.
        note.ShouldBe("Fixed duplicate charge on retry.");
    }

    [Fact]
    public async Task The_program_exposes_its_drafter_as_a_named_submodule_for_the_optimizers()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        await using var scope = pipeline.Services.CreateAsyncScope();
        var baseline = pipeline.Tuning(scope.ServiceProvider.GetRequiredService<AmpelumContext>()).Baseline();

        baseline.Submodules.Keys.ShouldBe([DraftProgram.DrafterName]);
        baseline.Rebind(baseline.Submodules).ShouldBeOfType<DraftProgram>().ShouldNotBeSameAs(baseline);
    }
}
