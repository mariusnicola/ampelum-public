using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class MiproV2Tests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- constructor ----------------------------------------------------------------------

    [Fact]
    public void Ctor_NullProgramRunner_Throws()
    {
        var chat = new FakeChatClient();
        Should.Throw<ArgumentNullException>(() =>
            new MiproV2<FakeProg>(null!, chat));
    }

    [Fact]
    public void Ctor_NullProposerClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex), null!));
    }

    [Fact]
    public void Ctor_MaxRoundsZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), maxRounds: 0));

    [Fact]
    public void Ctor_CandidatesZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), candidatesPerSubmodule: 0));

    [Fact]
    public void Ctor_MaxDemosNegative_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), maxBootstrappedDemos: -1));

    [Fact]
    public void Ctor_NumTrialsZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), numTrials: 0));

    [Fact]
    public void Ctor_NumDemoSetsZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), numDemoSets: 0));

    [Fact]
    public void Ctor_MinibatchSizeZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), minibatchSize: 0));

    [Fact]
    public void Ctor_FullEvalEveryZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MiproV2<FakeProg>((_, ex, _, _) => Task.FromResult(ex),
                new FakeChatClient(), fullEvalEvery: 0));

    [Fact]
    public void Ctor_ExistingCallStillWorks_AndNewSettingsHaveDocumentedDefaults()
    {
        var opt = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex), new FakeChatClient(),
            maxRounds: 4, candidatesPerSubmodule: 2, maxBootstrappedDemos: 1);

        opt.NumTrials.ShouldBe(4);
        opt.NumDemoSets.ShouldBe(3);
        opt.MinibatchSize.ShouldBe(25);
        opt.FullEvalEvery.ShouldBe(5);
        opt.Seed.ShouldBe(0);
    }

    [Fact]
    public void Ctor_ExplicitSettingsAreKept()
    {
        var opt = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex), new FakeChatClient(),
            maxRounds: 4, numTrials: 9, numDemoSets: 2, minibatchSize: 7, fullEvalEvery: 3, seed: 42);

        opt.NumTrials.ShouldBe(9);
        opt.NumDemoSets.ShouldBe(2);
        opt.MinibatchSize.ShouldBe(7);
        opt.FullEvalEvery.ShouldBe(3);
        opt.Seed.ShouldBe(42);
    }

    // ---- argument checks and cancellation --------------------------------------------------

    [Fact]
    public async Task CompileAsync_NullProgram_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(null!, MakeTrainset(1), new ExactMetric(),
                TestHelpers.MakeContext(), null, Ct));
    }

    [Fact]
    public async Task CompileAsync_NullTrainset_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), null!, new ExactMetric(),
                TestHelpers.MakeContext(), null, Ct));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), null!,
                TestHelpers.MakeContext(), null, Ct));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        var optimizer = MakeOptimizer();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), new ExactMetric(),
                null!, null, Ct));
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var optimizer = MakeOptimizer();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1),
                new ExactMetric(), TestHelpers.MakeContext(), null, cts.Token));
    }

    [Fact]
    public async Task Cancellation_inside_the_runner_propagates_and_is_not_counted_as_an_error()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var h = new Harness();
        // Calls 1 to 6 are the bootstrap runs; the first scoring call cancels.
        h.Fault = (_, _, n) =>
        {
            if (n != 7) return null;
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };
        var reports = new List<OptimizerProgress>();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Mipro(h, Proposer(1), candidates: 1, trials: 3, sets: 1)
                .CompileAsync(new QaProg(new QaSub()), MakeTrainset(6), new ExactMetric(),
                    TestHelpers.MakeContext(), new SyncProgress<OptimizerProgress>(reports.Add), cts.Token));

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_while_a_trial_starts_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var h = new Harness();
        var progress = new SyncProgress<OptimizerProgress>(r => { if (r.Step == 1) cts.Cancel(); });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Mipro(h, Proposer(1), candidates: 1, trials: 3, sets: 1)
                .CompileAsync(new QaProg(new QaSub()), MakeTrainset(4), new ExactMetric(),
                    TestHelpers.MakeContext(), progress, cts.Token));
    }

    // ---- demo candidates -------------------------------------------------------------------

    [Fact]
    public async Task Demo_sets_are_bootstrapped_from_differently_shuffled_trainsets()
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(2), candidates: 2, maxDemos: 1, sets: 4, trials: 20, mb: 8), n: 8);

        var demoSets = h.Calls.Select(c => c.Demos).Where(d => d != "-").Distinct().ToList();
        // One demo per set, and not every shuffle starts with the same example.
        demoSets.ShouldAllBe(d => !d.Contains('+') && d.StartsWith('q'));
        demoSets.Count.ShouldBeGreaterThan(1);
        demoSets.Count.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task Demo_sets_respect_the_demo_cap()
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(2), candidates: 2, maxDemos: 3, sets: 3, trials: 20, mb: 8), n: 8);

        var sizes = h.Calls.Select(c => c.Demos).Where(d => d != "-").Select(d => d.Split('+').Length).ToList();
        sizes.ShouldNotBeEmpty();
        sizes.ShouldAllBe(s => s == 3);
    }

    [Fact]
    public async Task The_zero_shot_set_and_the_current_instruction_are_candidates()
    {
        var h = new Harness { IsCorrect = (p, _, _) => Instr(p) == "-" && DemoSig(p) == "-" };

        var result = await CompileAsync(
            Mipro(h, Proposer(2), candidates: 2, sets: 2, trials: 40, mb: 6), n: 6);

        result.Score.ShouldBe(1.0);
        result.InstructionOverrides.ShouldBeEmpty();
        result.DemosBySubmodule.ShouldBeEmpty();
    }

    [Fact]
    public async Task Bootstrapped_demos_reach_the_result()
    {
        var h = new Harness { IsCorrect = (p, _, _) => DemoSig(p) != "-" };

        var result = await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, maxDemos: 2, sets: 2, trials: 20, mb: 6), n: 6);

        result.Score.ShouldBe(1.0);
        result.DemosBySubmodule["qa"].Count.ShouldBe(2);
        result.Program.ShouldNotBeNull();
        result.TrainsetSize.ShouldBe(6);
    }

    [Fact]
    public async Task Demos_traced_under_another_module_name_are_not_used_for_a_submodule()
    {
        var h = new Harness { TraceModule = "other", IsCorrect = (p, _, _) => DemoSig(p) != "-" };

        var result = await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, sets: 2, trials: 8, mb: 4), n: 4);

        result.DemosBySubmodule.ShouldBeEmpty();
        result.Score.ShouldBe(0.0);
    }

    [Fact]
    public async Task A_program_without_named_submodules_gets_the_demo_list_filed_under_its_trace_name()
    {
        var calls = new List<string>();
        var optimizer = new MiproV2<PlainProg>(
            (p, ex, ctx, _) =>
            {
                ctx.TraceSink.Write(Trace("classifier", ex));
                var demos = p.Ctx?.DefaultDemos;
                calls.Add(demos is { Count: > 0 } ? "demos" : "none");
                return Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?>
                {
                    ["a"] = demos is { Count: > 0 } ? ex.Outputs["a"] : "wrong",
                }));
            },
            Proposer(1), candidatesPerSubmodule: 1, maxBootstrappedDemos: 2,
            numTrials: 12, numDemoSets: 2, minibatchSize: 4, seed: 1);

        var result = await optimizer.CompileAsync(
            new PlainProg(), MakeTrainset(4), new ExactMetric(), TestHelpers.MakeContext(), null, Ct);

        result.Score.ShouldBe(1.0);
        result.DemosBySubmodule["__single__"].Count.ShouldBe(2);
        calls.ShouldContain("demos");
    }

    [Fact]
    public async Task A_program_without_named_submodules_and_no_traces_gets_the_labeled_demos()
    {
        var optimizer = new MiproV2<PlainProg>(
            (p, ex, _, _) => Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?>
            {
                ["a"] = p.Ctx?.DefaultDemos is { Count: > 0 } ? ex.Outputs["a"] : "wrong",
            })),
            Proposer(1), candidatesPerSubmodule: 1, maxBootstrappedDemos: 2,
            numTrials: 12, numDemoSets: 2, minibatchSize: 4, seed: 1);

        var result = await optimizer.CompileAsync(
            new PlainProg(), MakeTrainset(4), new ExactMetric(), TestHelpers.MakeContext(), null, Ct);

        result.Score.ShouldBe(1.0);
        result.DemosBySubmodule["__single__"].Count.ShouldBe(4);
    }

    // ---- instruction candidates ------------------------------------------------------------

    [Fact]
    public async Task The_proposer_is_called_once_for_the_summary_and_once_per_candidate()
    {
        var h = new Harness();
        var proposer = Proposer(3);

        await CompileAsync(Mipro(h, proposer, candidates: 3, trials: 2), n: 4);

        proposer.RecordedCalls.Count.ShouldBe(1 + 3);
    }

    [Fact]
    public async Task Every_submodule_gets_its_own_proposals()
    {
        var proposer = Proposer(4);
        var optimizer = new MiproV2<TwoProg>(
            (p, ex, ctx, _) => Task.FromResult(ex),
            proposer, candidatesPerSubmodule: 2, numTrials: 3, numDemoSets: 1);

        await optimizer.CompileAsync(
            new TwoProg(), MakeTrainset(3), new ExactMetric(), TestHelpers.MakeContext(), null, Ct);

        var prompts = proposer.RecordedCalls.Skip(1).Select(c => c.Messages.Last().Text).ToList();
        prompts.Count.ShouldBe(4);
        prompts.Count(p => p.Contains("Module: first")).ShouldBe(2);
        prompts.Count(p => p.Contains("Module: second")).ShouldBe(2);
    }

    [Fact]
    public async Task The_summary_call_sees_a_sample_of_the_trainset()
    {
        var proposer = Proposer(1);
        await CompileAsync(Mipro(new Harness(), proposer, candidates: 1, trials: 2), n: 4);

        var summaryCall = proposer.RecordedCalls[0];
        var text = string.Join('\n', summaryCall.Messages.Select(m => m.Text));
        text.ShouldContain("q=q0");
        text.ShouldContain("a=a0");
    }

    [Fact]
    public async Task The_summary_sample_is_capped()
    {
        var proposer = Proposer(1);
        await CompileAsync(Mipro(new Harness(), proposer, candidates: 1, trials: 2), n: 30);

        var text = string.Join('\n', proposer.RecordedCalls[0].Messages.Select(m => m.Text));
        text.Split('\n').Count(l => l.StartsWith("Input:")).ShouldBe(10);
    }

    [Fact]
    public async Task Every_proposal_prompt_carries_the_summary_the_module_name_examples_and_a_tip()
    {
        var proposer = Proposer(3);
        await CompileAsync(Mipro(new Harness(), proposer, candidates: 3, maxDemos: 2, trials: 2), n: 6);

        var proposals = proposer.RecordedCalls.Skip(1).ToList();
        proposals.Count.ShouldBe(3);
        foreach (var call in proposals)
        {
            var user = call.Messages.Last().Text;
            user.ShouldContain("Dataset summary: Summary of the data.");
            user.ShouldContain("Module: qa");
            user.ShouldContain("Input: q=q");
            user.ShouldContain("Output: a=a");
            MiproV2Tips.All.Count(t => user.Contains("Tip: " + t)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task Tips_come_from_the_seeded_rng_so_the_same_seed_picks_the_same_tips()
    {
        async Task<List<string>> Tips(int seed)
        {
            var proposer = Proposer(6);
            await CompileAsync(Mipro(new Harness(), proposer, candidates: 6, trials: 2, seed: seed), n: 4);
            return [.. proposer.RecordedCalls.Skip(1).Select(c => c.Messages.Last().Text.Split('\n').Single(l => l.StartsWith("Tip:")))];
        }

        (await Tips(3)).ShouldBe(await Tips(3));
        (await Tips(3)).ShouldNotBe(await Tips(4));
    }

    [Fact]
    public async Task An_empty_proposal_is_dropped_and_the_others_are_kept()
    {
        var h = new Harness { IsCorrect = (p, _, _) => Instr(p) == "Good one" };
        var proposer = new FakeChatClient().EnqueueText("summary").EnqueueText("  ").EnqueueText("Good one");

        var result = await CompileAsync(Mipro(h, proposer, candidates: 2, sets: 1, trials: 12, mb: 4), n: 4);

        result.InstructionOverrides["qa"].ShouldBe("Good one");
        h.Calls.ShouldAllBe(c => c.Instr == "-" || c.Instr == "Good one");
    }

    [Fact]
    public async Task A_proposer_failure_on_the_summary_becomes_an_optimizer_exception()
    {
        var proposer = new FakeChatClient().EnqueueError(new InvalidOperationException("boom"));

        var error = await Should.ThrowAsync<SignalGrid.Ampelum.Exceptions.OptimizerException>(() =>
            CompileAsync(Mipro(new Harness(), proposer, candidates: 1, trials: 2), n: 3));

        error.Message.ShouldContain("dataset summary");
        error.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_proposer_failure_on_a_proposal_names_the_submodule()
    {
        var proposer = new FakeChatClient().EnqueueText("summary").EnqueueError(new InvalidOperationException("boom"));

        var error = await Should.ThrowAsync<SignalGrid.Ampelum.Exceptions.OptimizerException>(() =>
            CompileAsync(Mipro(new Harness(), proposer, candidates: 1, trials: 2), n: 3));

        error.Message.ShouldContain("'qa'");
    }

    [Fact]
    public async Task A_cancelled_proposer_call_propagates_unwrapped()
    {
        var proposer = new FakeChatClient().EnqueueError(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CompileAsync(Mipro(new Harness(), proposer, candidates: 1, trials: 2), n: 3));
    }

    [Fact]
    public async Task A_hook_sees_every_proposer_call_under_a_MIPROv2_module_name()
    {
        var hook = new RecordingHook();
        var proposer = Proposer(3);
        var ctx = TestHelpers.MakeContext().WithHook(hook);

        await CompileAsync(Mipro(new Harness(), proposer, candidates: 3, trials: 2), n: 4, ctx: ctx);

        hook.Modules.Count.ShouldBe(proposer.RecordedCalls.Count);
        hook.Modules.ShouldAllBe(m => m.StartsWith("MIPROv2."));
        hook.Modules.Count(m => m == "MIPROv2.SummarizeDataset").ShouldBe(1);
        hook.Modules.Count(m => m == "MIPROv2.ProposeInstruction").ShouldBe(3);
    }

    // ---- search and evaluation -------------------------------------------------------------

    [Fact]
    public async Task One_pair_that_scores_one_is_found_within_the_trials()
    {
        // A first run where everything scores 1 lists the (instruction, demo set) pairs on offer.
        var probe = new Harness();
        await CompileAsync(
            Mipro(probe, Proposer(3), candidates: 3, maxDemos: 2, sets: 3, trials: 40, mb: 3, every: 5, seed: 7), n: 6);
        var pairs = probe.Calls.Select(c => (c.Instr, c.Demos)).Distinct().ToList();
        pairs.Count.ShouldBeGreaterThan(8);
        var target = pairs.Last(p => p.Instr != "-" && p.Demos != "-");

        var h = new Harness { IsCorrect = (p, _, _) => Instr(p) == target.Instr && DemoSig(p) == target.Demos };
        var result = await CompileAsync(
            Mipro(h, Proposer(3), candidates: 3, maxDemos: 2, sets: 3, trials: 40, mb: 3, every: 5, seed: 7), n: 6);

        result.Score.ShouldBe(1.0);
        result.InstructionOverrides["qa"].ShouldBe(target.Instr);
        string.Join('+', result.DemosBySubmodule["qa"].Select(d => d.Inputs["q"])).ShouldBe(target.Demos);
    }

    [Fact]
    public async Task The_search_concentrates_on_what_scores_well()
    {
        // Only the instruction decides the score. After the random phase the sampler should
        // favour it, so most later trials use it.
        var h = new Harness { IsCorrect = (p, _, _) => Instr(p) == "Instruction 2" };
        await CompileAsync(Mipro(h, Proposer(3), candidates: 3, sets: 1, trials: 40, mb: 2, every: 100, seed: 3), n: 2);

        var late = h.Calls.Skip(h.Calls.Count / 2).ToList();
        late.Count(c => c.Instr == "Instruction 2").ShouldBeGreaterThan(late.Count / 2);
    }

    [Fact]
    public async Task Each_trial_scores_a_minibatch_of_the_requested_size()
    {
        var h = new Harness();
        // 12 bootstrap runs, 4 trials of 5, one final full run of 12.
        await CompileAsync(Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 4, mb: 5, every: 100), n: 12);

        h.TotalCalls.ShouldBe(12 + 4 * 5 + 12);
    }

    [Fact]
    public async Task A_minibatch_larger_than_the_validation_set_uses_the_whole_set()
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 3, mb: 50, every: 100), n: 6);

        h.TotalCalls.ShouldBe(6 + 3 * 6 + 6);
    }

    [Fact]
    public async Task Minibatches_are_random_samples_not_the_first_examples()
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 8, mb: 3, every: 100), n: 12);

        // Eight minibatches of three, then the full run; the minibatch part is the first 24 bound calls.
        var minibatchQs = h.Calls.Take(24).Select(c => c.Q).Distinct().ToList();
        minibatchQs.Count.ShouldBeGreaterThan(3);
    }

    [Theory]
    [InlineData(2, 6, 3)]
    [InlineData(4, 6, 2)]
    [InlineData(100, 6, 1)]
    [InlineData(1, 3, 3)]
    public async Task A_full_evaluation_runs_every_fullEvalEvery_trials_and_at_the_end(int every, int trials, int fullRuns)
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: trials, mb: 5, every: every), n: 12);

        h.TotalCalls.ShouldBe(12 + trials * 5 + fullRuns * 12);
    }

    [Fact]
    public async Task The_result_is_the_best_full_evaluation_not_the_best_minibatch_and_not_the_last()
    {
        // Two examples, one demo set, one proposal, two trials with a full run after each.
        // Calls 1-2 bootstrap. Trial 1: call 3 minibatch (right), calls 4-5 full run (1 of 2 right).
        // Trial 2: call 6 minibatch (right), calls 7-8 full run (0 of 2 right).
        var h = new Harness { IsCorrect = (_, _, n) => n is 3 or 4 or 6 };

        var result = await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 2, mb: 1, every: 1), n: 2);

        h.TotalCalls.ShouldBe(8);
        result.Score.ShouldBe(0.5);
    }

    [Fact]
    public async Task The_validation_set_is_used_for_scoring_when_given()
    {
        var h = new Harness();
        var valset = Enumerable.Range(0, 4)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"v{i}" },
                new Dictionary<string, object?> { ["a"] = $"b{i}" }))
            .ToList();
        var optimizer = new MiproV2<QaProg>(
            h.RunAsync, Proposer(1), candidatesPerSubmodule: 1, maxBootstrappedDemos: 2,
            numTrials: 3, numDemoSets: 1, minibatchSize: 2, fullEvalEvery: 100, seed: 1, valset: valset);

        await CompileAsync(optimizer, n: 5);

        // Bootstrap runs on the 5 trainset rows (unbound, not recorded); scoring only sees the valset.
        h.Calls.ShouldAllBe(c => c.Q.StartsWith('v'));
        h.TotalCalls.ShouldBe(5 + 3 * 2 + 4);
    }

    [Fact]
    public async Task The_trainset_is_the_validation_set_when_none_is_given()
    {
        var h = new Harness();
        await CompileAsync(Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 2, mb: 50, every: 100), n: 3);

        h.Calls.ShouldAllBe(c => c.Q.StartsWith('q'));
        h.Calls.Count.ShouldBe(2 * 3 + 3);
    }

    // ---- determinism -----------------------------------------------------------------------

    [Fact]
    public async Task The_same_seed_and_model_give_the_same_result()
    {
        async Task<(string Summary, List<string> Calls, List<string> Prompts)> Run(int seed)
        {
            var h = new Harness { IsCorrect = (p, ex, _) => Instr(p) != "-" || ex.Inputs["q"]!.ToString()!.EndsWith('1') };
            var proposer = Proposer(3);
            var result = await CompileAsync(
                Mipro(h, proposer, candidates: 3, maxDemos: 2, sets: 3, trials: 15, mb: 4, every: 4, seed: seed), n: 9);
            var summary = $"{result.Score}|{string.Join(';', result.InstructionOverrides)}|" +
                string.Join(';', result.DemosBySubmodule.Select(kv => kv.Key + "=" + string.Join('+', kv.Value.Select(e => e.Inputs["q"]))));
            return (summary,
                [.. h.Calls.Select(c => $"{c.Instr}|{c.Demos}|{c.Q}")],
                [.. proposer.RecordedCalls.Select(c => c.Messages.Last().Text)]);
        }

        var a = await Run(5);
        var b = await Run(5);

        a.Summary.ShouldBe(b.Summary);
        a.Calls.ShouldBe(b.Calls);
        a.Prompts.ShouldBe(b.Prompts);
    }

    [Fact]
    public async Task A_different_seed_changes_the_run()
    {
        async Task<List<string>> Calls(int seed)
        {
            var h = new Harness();
            await CompileAsync(
                Mipro(h, Proposer(3), candidates: 3, maxDemos: 2, sets: 3, trials: 10, mb: 4, every: 4, seed: seed), n: 12);
            return [.. h.Calls.Select(c => $"{c.Instr}|{c.Demos}|{c.Q}")];
        }

        (await Calls(1)).ShouldNotBe(await Calls(2));
    }

    // ---- runner errors ---------------------------------------------------------------------

    [Fact]
    public async Task A_runner_exception_scores_zero_for_that_example_and_is_counted()
    {
        var h = new Harness();
        h.Fault = (p, ex, _) => p.Sub.Ctx is not null && (string)ex.Inputs["q"]! == "q2"
            ? new InvalidOperationException("model down")
            : null;
        var reports = new List<OptimizerProgress>();
        var stopped = new ConcurrentBag<Activity>();
        using var source = new ActivitySource("MiproV2Tests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("run");

        var result = await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 3, mb: 4, every: 100),
            n: 4, progress: new SyncProgress<OptimizerProgress>(reports.Add));

        // 3 trials of 4 plus one full run of 4: q2 is hit four times.
        h.Thrown.ShouldBe(4);
        result.Score.ShouldBe(0.75);
        reports.Count.ShouldBe(3);
        reports.Last().Extras["RunnerErrors"].ShouldBe(4);
        reports[0].Extras["RunnerErrors"].ShouldBe(1);
        var mine = stopped.Single(a => a.Parent == parent && (string?)a.GetTagItem("optimizer.type") == "MiproV2");
        mine.GetTagItem("optimizer.runner_errors").ShouldBe(4);
    }

    [Fact]
    public async Task A_metric_exception_is_counted_like_a_runner_exception()
    {
        var reports = new List<OptimizerProgress>();
        var optimizer = new MiproV2<QaProg>(
            new Harness().RunAsync, Proposer(1), candidatesPerSubmodule: 1,
            numTrials: 1, numDemoSets: 1, minibatchSize: 2, fullEvalEvery: 100);

        var result = await optimizer.CompileAsync(
            new QaProg(new QaSub()), MakeTrainset(2), new ThrowingMetric(), TestHelpers.MakeContext(),
            new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.Score.ShouldBe(0.0);
        reports.Single().Extras["RunnerErrors"].ShouldBe(4);
    }

    // ---- progress --------------------------------------------------------------------------

    [Fact]
    public async Task One_report_per_trial_with_the_trial_phase_and_the_best_score()
    {
        var reports = new List<OptimizerProgress>();
        var h = new Harness();

        var result = await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 4, mb: 3, every: 2),
            n: 6, progress: new SyncProgress<OptimizerProgress>(reports.Add));

        reports.Count.ShouldBe(4);
        reports.ShouldAllBe(r => r.Phase == "trial" && r.TotalSteps == 4);
        reports.Select(r => r.Step).ShouldBe([1, 2, 3, 4]);
        reports.ShouldAllBe(r => r.BestScore == 1.0);
        reports.Last().BestScore.ShouldBe(result.Score!.Value);
        reports.ShouldAllBe(r => (int)r.Extras["RunnerErrors"]! == 0);
    }

    [Fact]
    public async Task Progress_before_the_first_full_run_reports_the_best_minibatch_mean()
    {
        var reports = new List<OptimizerProgress>();
        var h = new Harness { IsCorrect = (_, ex, _) => (string)ex.Inputs["q"]! == "q0" };

        await CompileAsync(
            Mipro(h, Proposer(1), candidates: 1, sets: 1, trials: 3, mb: 6, every: 3),
            n: 6, progress: new SyncProgress<OptimizerProgress>(reports.Add));

        // Whole set every trial, one in six right.
        reports[0].BestScore.ShouldBe(1.0 / 6, 1e-9);
        reports[1].BestScore.ShouldBe(1.0 / 6, 1e-9);
    }

    // ---- degenerate input ------------------------------------------------------------------

    [Fact]
    public async Task CompileAsync_SingleTrial_ReturnsResult()
    {
        var proposerFake = new FakeChatClient();
        proposerFake.EnqueueText("Summary.");

        var optimizer = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex),
            proposerFake, maxRounds: 1, candidatesPerSubmodule: 1);

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(1), new ExactMetric(),
            TestHelpers.MakeContext(), null, Ct);

        result.Program.ShouldNotBeNull();
        result.Score.ShouldBe(1.0);
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_ReturnsZeroScore()
    {
        var proposerFake = new FakeChatClient();
        proposerFake.EnqueueText("Summary.");
        var optimizer = new MiproV2<FakeProg>(
            (_, ex, _, _) => Task.FromResult(ex),
            proposerFake, maxRounds: 1, candidatesPerSubmodule: 1);

        var result = await optimizer.CompileAsync(
            new FakeProg(), Array.Empty<Example>(), new ExactMetric(),
            TestHelpers.MakeContext(), null, Ct);

        result.Score.ShouldBe(0.0);
    }

    // ---- helpers ---------------------------------------------------------------------------

    static MiproV2<FakeProg> MakeOptimizer() =>
        new((_, ex, _, _) => Task.FromResult(ex), new FakeChatClient(),
            maxRounds: 1, candidatesPerSubmodule: 1);

    static MiproV2<QaProg> Mipro(
        Harness h,
        IChatClient proposer,
        int candidates = 2,
        int maxDemos = 2,
        int sets = 2,
        int trials = 6,
        int mb = 25,
        int every = 5,
        int seed = 1) =>
        new(h.RunAsync, proposer, candidatesPerSubmodule: candidates, maxBootstrappedDemos: maxDemos,
            numTrials: trials, numDemoSets: sets, minibatchSize: mb, fullEvalEvery: every, seed: seed);

    static Task<OptimizedProgram<QaProg>> CompileAsync(
        MiproV2<QaProg> optimizer,
        int n,
        IProgress<OptimizerProgress>? progress = null,
        AmpelumContext? ctx = null) =>
        optimizer.CompileAsync(
            new QaProg(new QaSub()), MakeTrainset(n), new ExactMetric(),
            ctx ?? TestHelpers.MakeContext(), progress, Ct);

    // One summary reply, then one reply per proposal.
    static FakeChatClient Proposer(int proposals)
    {
        var fake = new FakeChatClient().EnqueueText("Summary of the data.");
        for (int i = 1; i <= proposals; i++) fake.EnqueueText($"Instruction {i}");
        return fake;
    }

    static List<Example> MakeTrainset(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    static string Instr(QaProg p) =>
        p.Sub.Ctx?.InstructionOverrides?.GetValueOrDefault("qa") ?? "-";

    static string DemoSig(QaProg p) =>
        p.Sub.Ctx?.DefaultDemos is { Count: > 0 } demos
            ? string.Join('+', demos.Select(d => d.Inputs["q"]))
            : "-";

    static TraceRecord Trace(string module, Example ex) =>
        new(
            Id: Guid.NewGuid(),
            ModuleName: module,
            SignatureType: typeof(string),
            Inputs: ex.Inputs,
            Outputs: ex.Outputs,
            PromptMessages: [],
            RawResponse: "",
            Usage: null,
            Duration: TimeSpan.Zero,
            Error: null,
            Metadata: new Dictionary<string, object?>());

    sealed class Harness
    {
        public int TotalCalls { get; private set; }
        public int Thrown { get; private set; }
        public List<(string Instr, string Demos, string Q)> Calls { get; } = [];
        public string TraceModule { get; init; } = "qa";
        public Func<QaProg, Example, int, bool> IsCorrect { get; init; } = (_, _, _) => true;
        public Func<QaProg, Example, int, Exception?>? Fault { get; set; }

        public Task<Example> RunAsync(QaProg p, Example ex, AmpelumContext ctx, CancellationToken ct)
        {
            TotalCalls++;
            // A program that was never bound has no context: that is a bootstrap run.
            if (p.Sub.Ctx is not null)
                Calls.Add((Instr(p), DemoSig(p), (string)ex.Inputs["q"]!));
            ctx.TraceSink.Write(Trace(TraceModule, ex));

            var fault = Fault?.Invoke(p, ex, TotalCalls);
            if (fault is not null)
            {
                if (fault is not OperationCanceledException) Thrown++;
                throw fault;
            }

            var answer = IsCorrect(p, ex, TotalCalls) ? ex.Outputs["a"] : "wrong";
            return Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?> { ["a"] = answer }));
        }
    }

    sealed class QaSub(AmpelumContext? ctx = null)
    {
        public AmpelumContext? Ctx { get; } = ctx;
        public QaSub WithContext(AmpelumContext c) => new(c);
    }

    sealed class QaProg(QaSub sub) : IComposedModule
    {
        public QaSub Sub { get; } = sub;
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["qa"] = Sub };
        public object Rebind(IReadOnlyDictionary<string, object> s) => new QaProg((QaSub)s["qa"]);
    }

    sealed class TwoProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["first"] = new QaSub(), ["second"] = new QaSub() };
        public object Rebind(IReadOnlyDictionary<string, object> s) => this;
    }

    sealed class PlainProg(AmpelumContext? ctx = null)
    {
        public AmpelumContext? Ctx { get; } = ctx;
        public PlainProg WithContext(AmpelumContext c) => new(c);
    }

    sealed class RecordingHook : IHook
    {
        public ConcurrentBag<string> Modules { get; } = [];
        public int Order => 0;
        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Modules.Add(ctx.ModuleName);
            return ValueTask.CompletedTask;
        }
        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    { public void Report(T value) => cb(value); }

    sealed class ExactMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            Equals(((IReadOnlyDictionary<string, object?>)actual)["a"],
                   ((IReadOnlyDictionary<string, object?>)expected)["a"]) ? 1.0 : 0.0;
    }

    sealed class ThrowingMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            throw new InvalidOperationException("metric failed");
    }

    sealed class FakeProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }
}
