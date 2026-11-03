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

    static GepaOptions Options(int seed) => PromptTuning.DefaultOptions with { Seed = seed };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Gepa_asks_the_proposer_with_the_approved_note_and_the_winner_beats_the_baseline(int seed)
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());

        var result = await pipeline.TuneAsync(Options(seed), Ct);

        var asks = model.Calls.Where(c => c.System.Contains("prompt engineer")).Select(c => c.User).ToList();
        asks.ShouldContain(a => a.StartsWith("Module: feature"));
        asks.ShouldContain(a => a.StartsWith("Module: fix"));
        // The proposer is shown the approved note next to the note the program wrote, and the current instruction.
        asks.ShouldAllBe(a => a.Contains("Feedback: The approved note reads \""));
        asks.ShouldContain(a => a.Contains("Current instruction:\n" + Helpers.DraftPhrase));
        result.BaselineRougeL.ShouldBe(0.326, 0.001);
        result.TunedRougeL.ShouldBeGreaterThan(result.BaselineRougeL + 0.05);
        result.Program.Score!.Value.ShouldBeGreaterThan(0.0);
        result.Program.InstructionOverrides.Count.ShouldBeGreaterThan(0);
        result.Program.InstructionOverrides.Values.ShouldAllBe(v => v == DraftingRules.TunedInstruction);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task Gepa_keeps_several_candidates_on_the_frontier_and_merges_two_that_changed_different_submodules(int seed)
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        var reports = new List<OptimizerProgress>();

        await pipeline.TuneAsync(Options(seed), Ct, new SyncProgress(reports.Add));

        reports.Select(r => r.Phase).Distinct().ShouldBe(["baseline", "iteration"]);
        var last = reports.Last();
        ((int)last.Extras["Merges"]!).ShouldBeGreaterThan(0);
        ((int)last.Extras["Candidates"]!).ShouldBeGreaterThan(3);
        ((int)last.Extras["FrontierSize"]!).ShouldBeGreaterThan(1);
        last.Extras["RunnerErrors"].ShouldBe(0);
        // A merge is tried at every second iteration, so the first one that joined the pool is at an even step.
        (reports.First(r => (int)r.Extras["Merges"]! > 0).Step % 2).ShouldBe(0);
    }

    [Fact]
    public async Task The_tuned_program_sends_the_tuned_instruction_for_a_tuned_kind_and_the_baseline_sends_none()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        var tuned = (await pipeline.TuneAsync(Options(0), Ct)).Program;
        await using var scope = pipeline.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        var baseline = pipeline.Tuning(ctx).GepaBaseline();

        var before = model.Calls.Count;
        var note = (await tuned.Bind(ctx).ForwardAsync(Input(), Ct)).Value.Note;
        var tunedCall = model.Calls.Skip(before).Single();
        before = model.Calls.Count;
        await baseline.ForwardAsync(Input(), Ct);
        var baselineCall = model.Calls.Skip(before).Single();

        tuned.InstructionOverrides.ShouldContainKey(KindRoutedProgram.NameOf(ChangeKind.Fix));
        tunedCall.System.ShouldContain(DraftingRules.TunedInstruction);
        note.ShouldBe("Fixed duplicate charge on retry.");
        baselineCall.System.ShouldContain(Helpers.DraftPhrase);
        baselineCall.System.ShouldNotContain(DraftingRules.TunedInstruction);
    }

    [Fact]
    public async Task The_routed_program_exposes_one_named_submodule_per_kind_and_drafts_with_the_one_of_the_change()
    {
        var model = ReleaseScript.Create();
        await using var pipeline = new ReleaseNotesPipeline(model, Rig.Source(), Rig.Options());
        await using var scope = pipeline.Services.CreateAsyncScope();
        var baseline = pipeline.Tuning(scope.ServiceProvider.GetRequiredService<AmpelumContext>()).GepaBaseline();

        baseline.Submodules.Keys.ShouldBe(["feature", "fix", "breaking", "internal"], ignoreOrder: true);
        baseline.Rebind(baseline.Submodules).ShouldBeOfType<KindRoutedProgram>().ShouldNotBeSameAs(baseline);
        await Should.ThrowAsync<ArgumentException>(() =>
            baseline.ForwardAsync(Input() with { Kind = "Unknown" }, Ct));
    }

    sealed class SyncProgress(Action<OptimizerProgress> cb) : IProgress<OptimizerProgress>
    {
        public void Report(OptimizerProgress value) => cb(value);
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
