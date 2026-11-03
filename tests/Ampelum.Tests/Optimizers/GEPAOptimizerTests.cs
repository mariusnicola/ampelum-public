using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Hooks;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class GEPAOptimizerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A seed under which the second iteration of the two-submodule world draws the unchanged program as parent.
    const int MergeSeed = 1;

    [Signature("Given a question, produce an answer.")]
    public sealed record QASig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    // ---- constructor and arguments ---------------------------------------------------------

    [Fact]
    public void Ctor_NullRunner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new GEPAOptimizer<Prog>(null!, new FakeChatClient()));

    [Fact]
    public void Ctor_NullReflectionClient_Throws() =>
        Should.Throw<ArgumentNullException>(() => new GEPAOptimizer<Prog>(new World().RunAsync, null!));

    [Theory]
    [InlineData(-1, 1, 1, 1)]
    [InlineData(1, -1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void Ctor_OutOfRangeOption_Throws(int iterations, int calls, int minibatch, int mergeEvery) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new GEPAOptimizer<Prog>(
            new World().RunAsync, new FakeChatClient(),
            new GepaOptions
            {
                MaxIterations = iterations, MaxMetricCalls = calls, MinibatchSize = minibatch, MergeEvery = mergeEvery,
            }));

    [Fact]
    public void Ctor_EmptyValset_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => new GEPAOptimizer<Prog>(
            new World().RunAsync, new FakeChatClient(), valset: []));
        ex.ParamName.ShouldBe("valset");
    }

    [Fact]
    public void Options_have_documented_defaults()
    {
        var d = new GepaOptions();
        d.MaxIterations.ShouldBe(10);
        d.MaxMetricCalls.ShouldBe(200);
        d.MinibatchSize.ShouldBe(3);
        d.UseMerge.ShouldBeTrue();
        d.MergeEvery.ShouldBe(3);
        d.ValidationHoldoutPercent.ShouldBe(20);
        d.Seed.ShouldBe(0);

        new GEPAOptimizer<Prog>(new World().RunAsync, new FakeChatClient()).Options.ShouldBe(d);
        var custom = new GepaOptions { Seed = 7 };
        new GEPAOptimizer<Prog>(new World().RunAsync, new FakeChatClient(), custom).Options.ShouldBeSameAs(custom);
    }

    [Fact]
    public async Task CompileAsync_NullArguments_Throw()
    {
        var sut = new GEPAOptimizer<Prog>(new World().RunAsync, new FakeChatClient());
        var ctx = TestHelpers.MakeContext();
        var train = Samples("t", 1);

        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(null!, train, new Exact(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog("first"), null!, new Exact(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog("first"), train, null!, ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog("first"), train, new Exact(), null!, ct: Ct));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_Throws()
    {
        var sut = new GEPAOptimizer<Prog>(new World().RunAsync, new FakeChatClient());

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            sut.CompileAsync(new Prog("first"), [], new Exact(), TestHelpers.MakeContext(), ct: Ct));

        ex.ParamName.ShouldBe("trainset");
    }

    // ---- frontier and parent sampling (hand-built tables) -----------------------------------

    [Fact]
    public void Frontier_is_every_candidate_that_is_best_on_at_least_one_example()
    {
        // Example 0 is best for candidates 0 and 3 (tie), example 1 for 1 and 3, example 2 for candidate 2 alone.
        var table = new[]
        {
            new[] { 1.0, 0.0, 0.1 },
            new[] { 0.0, 1.0, 0.1 },
            new[] { 0.5, 0.5, 0.5 },
            new[] { 1.0, 1.0, 0.0 },
            new[] { 0.2, 0.2, 0.2 },
        };

        var weights = GEPAOptimizer<Prog>.FrontierWeights(table);

        weights.ShouldBe([1, 1, 1, 2, 0]);
        // Candidate 3 has the best mean, yet it is not the whole frontier.
        table.Select(s => s.Average()).ToList().IndexOf(table.Max(s => s.Average())).ShouldBe(3);
    }

    [Fact]
    public void Parent_is_drawn_in_proportion_to_the_number_of_examples_a_candidate_is_best_on()
    {
        int[] weights = [0, 3, 1];
        var rng = new Random(11);
        var counts = new int[3];

        for (int i = 0; i < 4000; i++) counts[GEPAOptimizer<Prog>.PickParent(weights, rng)]++;

        counts[0].ShouldBe(0);
        ((double)counts[1] / 4000).ShouldBe(0.75, 0.03);
        ((double)counts[2] / 4000).ShouldBe(0.25, 0.03);

        var one = new Random(11);
        var two = new Random(11);
        Enumerable.Range(0, 20).Select(_ => GEPAOptimizer<Prog>.PickParent(weights, two)).ToList()
            .ShouldBe(Enumerable.Range(0, 20).Select(_ => GEPAOptimizer<Prog>.PickParent(weights, one)).ToList());
    }

    // ---- merge ------------------------------------------------------------------------------

    [Fact]
    public void Merge_takes_each_submodule_from_the_side_that_changed_it()
    {
        var ancestor = Cand(0, [], []);
        var a = Cand(1, [0], [], ("first", "A1"));
        var b = Cand(2, [0], [], ("second", "B1"));

        var merged = GEPAOptimizer<Prog>.MergeInstructions(a, b, ancestor, ["first", "second", "third"]);

        merged.ShouldBe(new Dictionary<string, string> { ["first"] = "A1", ["second"] = "B1" });
    }

    [Fact]
    public void Merge_keeps_the_ancestors_text_for_a_submodule_neither_side_changed()
    {
        var ancestor = Cand(1, [0], [], ("first", "A0"));
        var a = Cand(2, [1], [], ("first", "A0"), ("second", "S1"));
        var b = Cand(3, [1], [], ("first", "A0"));

        var merged = GEPAOptimizer<Prog>.MergeInstructions(a, b, ancestor, ["first", "second"]);

        merged.ShouldBe(new Dictionary<string, string> { ["first"] = "A0", ["second"] = "S1" });
    }

    [Fact]
    public void Merge_prefers_the_better_side_when_both_changed_the_same_submodule()
    {
        var ancestor = Cand(0, [], []);
        var better = Cand(1, [0], [0.9], ("first", "good"));
        var worse = Cand(2, [0], [0.2], ("first", "bad"));

        GEPAOptimizer<Prog>.MergeInstructions(better, worse, ancestor, ["first"])["first"].ShouldBe("good");
        GEPAOptimizer<Prog>.MergeInstructions(worse, better, ancestor, ["first"])["first"].ShouldBe("good");
    }

    [Fact]
    public void Ancestors_follow_every_parent_once()
    {
        var pool = new List<GEPAOptimizer<Prog>.Candidate>
        {
            Cand(0, [], []),
            Cand(1, [0], []),
            Cand(2, [0], []),
            Cand(3, [1, 2], []),
        };

        GEPAOptimizer<Prog>.AncestorsOf(pool, pool[3]).OrderBy(i => i).ShouldBe([0, 1, 2]);
        GEPAOptimizer<Prog>.AncestorsOf(pool, pool[0]).ShouldBeEmpty();
    }

    // ---- the search -------------------------------------------------------------------------

    [Fact]
    public async Task A_child_that_does_not_beat_its_parent_on_the_minibatch_is_never_run_on_the_validation_set()
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") is null };
        var reflect = new FakeChatClient().EnqueueText("worse");

        var result = await Optimizer(world, reflect, Opts(iterations: 1), Val("v", 4))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        world.Calls.Count(c => c.StartsWith("v")).ShouldBe(4);
        result.InstructionOverrides.ShouldBeEmpty();
        result.Score.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_child_that_beats_its_parent_is_scored_on_the_validation_set_and_can_win()
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") == "better" };
        var reflect = new FakeChatClient().EnqueueText("better");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(iterations: 1), Val("v", 4))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        world.Calls.Count(c => c.StartsWith("v")).ShouldBe(8);
        result.InstructionOverrides.ShouldBe(new Dictionary<string, string> { ["first"] = "better" });
        result.Score.ShouldBe(1.0);
        result.TrainsetSize.ShouldBe(3);
        result.DemosBySubmodule.ShouldBeEmpty();
        reports.Select(r => r.Phase).ShouldBe(["baseline", "iteration"]);
        reports[0].BestScore.ShouldBe(0.0);
        reports[1].BestScore.ShouldBe(1.0);
        reports[1].Step.ShouldBe(1);
        reports[1].TotalSteps.ShouldBe(1);
        reports[1].Extras["Candidates"].ShouldBe(2);
        reports[1].Extras["FrontierSize"].ShouldBe(1);
        reports[1].Extras["Merges"].ShouldBe(0);
        reports[1].Extras["RunnerErrors"].ShouldBe(0);
        reports[1].Extras["MetricCalls"].ShouldBe(world.Calls.Count);
    }

    [Fact]
    public async Task The_result_is_the_candidate_with_the_best_mean_validation_score()
    {
        // "one" is right on half of the examples and "two" on all of them, whichever parent proposes it.
        var world = new World
        {
            Correct = (p, ex) => p.Instruction("first") == "two"
                || (p.Instruction("first") == "one" && Q(ex) is "t0" or "t1" or "v0" or "v1"),
        };
        var reflect = new FakeChatClient().EnqueueText("one").EnqueueText("two");

        var result = await Optimizer(world, reflect, Opts(iterations: 2, minibatch: 3, merge: false), Val("v", 4))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        result.Score.ShouldBe(1.0);
        result.InstructionOverrides["first"].ShouldBe("two");
    }

    [Fact]
    public async Task A_proposal_that_scores_the_same_as_the_best_does_not_replace_it()
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") is not null };
        var reflect = new FakeChatClient().EnqueueText("one").EnqueueText("two");

        var result = await Optimizer(world, reflect, Opts(iterations: 2), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        result.InstructionOverrides["first"].ShouldBe("one");
    }

    [Fact]
    public async Task A_blank_proposal_adds_no_child()
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") is not null };
        var reflect = new FakeChatClient().EnqueueText("   ");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(iterations: 1), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.InstructionOverrides.ShouldBeEmpty();
        reports.Last().Extras["Candidates"].ShouldBe(1);
    }

    [Fact]
    public async Task Submodules_are_changed_round_robin()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b").EnqueueText("c");

        await Optimizer(world, reflect, Opts(iterations: 3, merge: false), Val("v", 1))
            .CompileAsync(new Prog("first", "second"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        var modules = reflect.RecordedCalls.Select(c => c.Messages.Last().Text.Split('\n')[0]).ToList();
        modules.ShouldBe(["Module: first", "Module: second", "Module: first"]);
    }

    [Fact]
    public async Task Two_siblings_that_changed_different_submodules_are_merged_and_the_merge_wins()
    {
        var world = new World { Correct = XyWorld() };
        var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(iterations: 3, minibatch: 4, seed: MergeSeed), Val("v", 4, "fs"))
            .CompileAsync(new Prog("first", "second"), Samples("t", 4, "fs"), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reflect.RecordedCalls.Count.ShouldBe(2);
        result.InstructionOverrides.ShouldBe(new Dictionary<string, string> { ["first"] = "X", ["second"] = "Y" });
        result.Score.ShouldBe(1.0);
        reports.Last().Extras["Merges"].ShouldBe(1);
        reports.Last().Extras["Candidates"].ShouldBe(4);
    }

    [Fact]
    public async Task A_merge_that_does_not_beat_its_parents_on_the_minibatch_is_not_added()
    {
        var world = new World { Correct = XyWorld(interference: true) };
        var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(iterations: 3, minibatch: 4, seed: MergeSeed), Val("v", 4, "fs"))
            .CompileAsync(new Prog("first", "second"), Samples("t", 4, "fs"), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reflect.RecordedCalls.Count.ShouldBe(2);
        reports.Last().Extras["Merges"].ShouldBe(0);
        reports.Last().Extras["Candidates"].ShouldBe(3);
        result.InstructionOverrides.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Merging_can_be_switched_off()
    {
        var world = new World { Correct = XyWorld() };
        var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y").EnqueueText("Z");

        await Optimizer(world, reflect, Opts(iterations: 3, minibatch: 4, seed: MergeSeed, merge: false), Val("v", 4, "fs"))
            .CompileAsync(new Prog("first", "second"), Samples("t", 4, "fs"), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        reflect.RecordedCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task With_no_pair_to_merge_the_iteration_mutates()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("X");

        await Optimizer(world, reflect, Opts(iterations: 1, mergeEvery: 1), Val("v", 1))
            .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        reflect.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_pair_is_merged_only_once()
    {
        var world = new World { Correct = XyWorld(interference: true) };
        var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y").EnqueueText("Z");

        await Optimizer(world, reflect, Opts(iterations: 4, minibatch: 4, seed: MergeSeed, mergeEvery: 1), Val("v", 4, "fs"))
            .CompileAsync(new Prog("first", "second"), Samples("t", 4, "fs"), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        // Iterations 1 and 2 have nothing to merge and mutate. Iteration 3 tries the pair and iteration 4 has
        // no other pair, so it mutates again.
        reflect.RecordedCalls.Count.ShouldBe(3);
    }

    // ---- budget -----------------------------------------------------------------------------

    [Fact]
    public async Task The_search_stops_once_the_metric_call_budget_is_spent()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b").EnqueueText("c");

        await Optimizer(world, reflect, Opts(iterations: 50, minibatch: 2, calls: 6), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 4), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        // 2 baseline runs, then one iteration of 2 parent runs and 2 child runs reaches the budget.
        world.Calls.Count.ShouldBe(6);
        reflect.RecordedCalls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(14)]
    public async Task No_metric_call_is_made_once_the_budget_is_reached(int budget)
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") is not null };
        var reflect = new FakeChatClient();
        for (int i = 0; i < 30; i++) reflect.EnqueueText($"proposal {i}");
        var reports = new List<OptimizerProgress>();

        await Optimizer(world, reflect, Opts(iterations: 30, minibatch: 2, calls: budget, merge: false), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 4), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        world.Calls.Count.ShouldBeLessThanOrEqualTo(budget);
        ((int)reports.Last().Extras["MetricCalls"]!).ShouldBeLessThanOrEqualTo(budget);
    }

    [Fact]
    public async Task A_candidate_whose_validation_run_is_cut_short_is_not_added_and_the_best_complete_one_wins()
    {
        // The child is better, but the budget ends after its first validation run.
        var world = new World { Correct = (p, ex) => p.Instruction("first") is not null };
        var reflect = new FakeChatClient().EnqueueText("better");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(iterations: 5, minibatch: 2, calls: 7), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 4), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        world.Calls.Count.ShouldBe(7);
        result.InstructionOverrides.ShouldBeEmpty();
        result.Score.ShouldBe(0.0);
        reports.Single().Phase.ShouldBe("baseline");
    }

    [Fact]
    public async Task A_budget_smaller_than_one_validation_run_is_rejected()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            Optimizer(new World(), new FakeChatClient(), Opts(iterations: 1, calls: 3), Val("v", 4))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct));

        ex.ParamName.ShouldBe("trainset");
    }

    [Fact]
    public async Task The_search_stops_after_the_iteration_limit()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b").EnqueueText("c");

        await Optimizer(world, reflect, Opts(iterations: 2, merge: false), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 4), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        reflect.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Zero_iterations_returns_the_unchanged_program_scored_on_the_validation_set()
    {
        var world = new World { Correct = (p, ex) => Q(ex) == "v0" };
        var reflect = new FakeChatClient();

        var result = await Optimizer(world, reflect, Opts(iterations: 0), Val("v", 4))
            .CompileAsync(new Prog("first"), Samples("t", 4), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        result.Score.ShouldBe(0.25);
        result.InstructionOverrides.ShouldBeEmpty();
        reflect.RecordedCalls.ShouldBeEmpty();
    }

    // ---- determinism ------------------------------------------------------------------------

    [Fact]
    public async Task The_same_seed_gives_the_same_runs_and_the_same_result()
    {
        async Task<(List<string> Calls, OptimizedProgram<Prog> Result)> Go(int seed)
        {
            var world = new World { Correct = XyWorld() };
            var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y").EnqueueText("Z");
            // No valset: the split and the minibatches both come from the seed.
            var result = await new GEPAOptimizer<Prog>(world.RunAsync, reflect,
                    new GepaOptions { MaxIterations = 3, MinibatchSize = 2, Seed = seed })
                .CompileAsync(new Prog("first", "second"), Samples("t", 10, "fs"), new Exact(),
                    TestHelpers.MakeContext(), ct: Ct);
            return (world.Calls, result);
        }

        var one = await Go(5);
        var two = await Go(5);
        var other = await Go(6);

        two.Calls.ShouldBe(one.Calls);
        two.Result.InstructionOverrides.ShouldBe(one.Result.InstructionOverrides);
        two.Result.Score.ShouldBe(one.Result.Score);
        other.Calls.ShouldNotBe(one.Calls);
    }

    // ---- validation split -------------------------------------------------------------------

    [Theory]
    [InlineData(10, 20, 2)]
    [InlineData(5, 20, 1)]
    [InlineData(4, 100, 3)]
    [InlineData(2, 0, 1)]
    [InlineData(1, 20, 1)]
    public async Task Without_a_valset_the_trainset_is_split_with_a_holdout(int n, int percent, int expectedValidation)
    {
        var world = new World { Correct = (p, ex) => true };

        await new GEPAOptimizer<Prog>(world.RunAsync, new FakeChatClient(),
                new GepaOptions { MaxIterations = 0, ValidationHoldoutPercent = percent })
            .CompileAsync(new Prog("first"), Samples("t", n), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        world.Calls.Count.ShouldBe(expectedValidation);
    }

    [Fact]
    public async Task The_split_keeps_validation_examples_out_of_the_minibatches()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a");

        await new GEPAOptimizer<Prog>(world.RunAsync, reflect,
                new GepaOptions { MaxIterations = 1, MinibatchSize = 100, UseMerge = false })
            .CompileAsync(new Prog("first"), Samples("t", 5), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        var validation = world.Calls[0];
        world.Calls.Skip(1).ShouldNotContain(c => c == validation);
        world.Calls.Skip(1).Select(c => c.Split('|')[0]).Distinct().Count().ShouldBe(4);
    }

    // ---- reflection prompt and hooks --------------------------------------------------------

    [Fact]
    public async Task The_reflection_prompt_carries_the_instruction_the_inputs_the_outputs_and_the_feedback()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a");

        await Optimizer(world, reflect, Opts(iterations: 1, minibatch: 2), Val("v", 1))
            .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        var prompt = reflect.RecordedCalls.Single().Messages.Last().Text;
        prompt.ShouldContain("Module: first");
        prompt.ShouldContain("(the module's own instruction)");
        prompt.ShouldContain("Inputs: q=t0");
        prompt.ShouldContain("Inputs: q=t1");
        prompt.ShouldContain("Outputs: a=wrong");
        prompt.ShouldContain("Score: 0");
        // The metric is a plain IMetric: the feedback is built from the expected and the actual outputs.
        prompt.ShouldContain("Expected a=t0, got a=wrong.");
    }

    [Fact]
    public async Task A_feedback_metric_supplies_the_feedback_text()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a");

        await Optimizer(world, reflect, Opts(iterations: 1, minibatch: 1), Val("v", 1))
            .CompileAsync(new Prog("first"), Samples("t", 1), new Feedback(), TestHelpers.MakeContext(), ct: Ct);

        var prompt = reflect.RecordedCalls.Single().Messages.Last().Text;
        prompt.ShouldContain("Feedback: the answer was wrong, wanted t0");
        prompt.ShouldNotContain("Expected a=");
    }

    [Fact]
    public async Task The_prompt_shows_the_signature_instruction_of_an_unchanged_predict_submodule()
    {
        var reflect = new FakeChatClient().EnqueueText("a");
        var sut = new GEPAOptimizer<SigProg>(
            (p, ex, c, t) => Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?> { ["a"] = "wrong" })),
            reflect, Opts(iterations: 1), Val("v", 1));

        await sut.CompileAsync(new SigProg(), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        reflect.RecordedCalls.Single().Messages.Last().Text
            .ShouldContain("Current instruction:\nGiven a question, produce an answer.\n");
    }

    [Fact]
    public async Task The_prompt_shows_the_override_of_the_parent_once_a_child_was_accepted()
    {
        var world = new World { Correct = (p, ex) => p.Instruction("first") == "improved" };
        var reflect = new FakeChatClient().EnqueueText("improved").EnqueueText("again");

        await Optimizer(world, reflect, Opts(iterations: 2, merge: false), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        // The accepted child is best everywhere, so it is the only parent left on the frontier.
        reflect.RecordedCalls[1].Messages.Last().Text.ShouldContain("Current instruction:\nimproved\n");
    }

    [Fact]
    public async Task A_hook_sees_every_reflection_call()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b");
        var hook = new SpyHook();
        var ctx = TestHelpers.MakeContext() with { Hooks = [hook] };

        await Optimizer(world, reflect, Opts(iterations: 2, merge: false), Val("v", 1))
            .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), ctx, ct: Ct);

        hook.Modules.ShouldBe(["GEPA.Reflect", "GEPA.Reflect"]);
        hook.Modules.ShouldAllBe(m => m.StartsWith("GEPA."));
    }

    [Fact]
    public async Task The_reflection_call_goes_to_the_reflection_client_not_to_the_context_client()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a");
        var contextClient = new FakeChatClient();

        await Optimizer(world, reflect, Opts(iterations: 1), Val("v", 1))
            .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(contextClient), ct: Ct);

        reflect.RecordedCalls.Count.ShouldBe(1);
        contextClient.RecordedCalls.ShouldBeEmpty();
    }

    // ---- errors -----------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_runner_scores_zero_is_counted_and_the_metric_never_sees_the_expected_outputs_as_actual()
    {
        var world = new World { Fault = (p, ex) => new InvalidOperationException("boom") };
        var metric = new Exact();
        var reflect = new FakeChatClient().EnqueueText("a");
        var reports = new List<OptimizerProgress>();
        var stopped = new ConcurrentBag<Activity>();
        using var source = new ActivitySource("GEPAOptimizerTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("run");

        var result = await Optimizer(world, reflect, Opts(iterations: 1, minibatch: 2), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 3), metric, TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.Score.ShouldBe(0.0);
        metric.Calls.ShouldBe(0);
        // 2 validation runs, 2 parent runs and 2 child runs.
        reports.Last().Extras["RunnerErrors"].ShouldBe(6);
        var prompt = reflect.RecordedCalls.Single().Messages.Last().Text;
        prompt.ShouldContain("Outputs: (none)");
        prompt.ShouldContain("Feedback: The run failed: boom");
        var mine = stopped.Single(a => a.Parent == parent && (string?)a.GetTagItem("optimizer.type") == "GEPAOptimizer");
        mine.GetTagItem("optimizer.runner_errors").ShouldBe(6);
        mine.GetTagItem("optimizer.final_score").ShouldBe(0.0);
        mine.GetTagItem("optimizer.metric_calls").ShouldBe(6);
    }

    [Fact]
    public async Task A_metric_exception_is_counted_like_a_runner_exception()
    {
        var reports = new List<OptimizerProgress>();

        var result = await new GEPAOptimizer<Prog>(new World().RunAsync, new FakeChatClient(),
                new GepaOptions { MaxIterations = 0 })
            .CompileAsync(new Prog("first"), Samples("t", 3), new ThrowingMetric(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.Score.ShouldBe(0.0);
        reports.Single().Extras["RunnerErrors"].ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_reflection_call_is_wrapped_in_an_optimizer_exception()
    {
        var reflect = new FakeChatClient().EnqueueError(new InvalidOperationException("model down"));

        var ex = await Should.ThrowAsync<OptimizerException>(() =>
            Optimizer(new World(), reflect, Opts(iterations: 1), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct));

        ex.Message.ShouldContain("first");
        ex.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancellation_before_the_start_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(new World(), new FakeChatClient(), Opts(iterations: 1), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_inside_the_runner_propagates_and_is_not_counted_as_an_error()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reports = new List<OptimizerProgress>();
        var world = new World
        {
            Fault = (p, ex) =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            },
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(world, new FakeChatClient(), Opts(iterations: 1), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(),
                    new SyncProgress<OptimizerProgress>(reports.Add), cts.Token));

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_between_iterations_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b");

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(world, reflect, Opts(iterations: 5, merge: false), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(),
                    new SyncProgress<OptimizerProgress>(r => { if (r.Step == 1) cts.Cancel(); }), cts.Token));

        reflect.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_inside_the_reflection_call_propagates_unwrapped()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(new World(), new CancellingClient(cts), Opts(iterations: 1), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: cts.Token));
    }

    [Fact]
    public async Task A_timeout_in_the_runner_that_does_not_cancel_the_token_scores_zero_and_is_counted()
    {
        var world = new World { Fault = (p, ex) => new TaskCanceledException("timed out") };
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, new FakeChatClient(), Opts(iterations: 0), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.Score.ShouldBe(0.0);
        reports.Single().Extras["RunnerErrors"].ShouldBe(2);
    }

    [Fact]
    public async Task A_timeout_in_the_reflection_call_that_does_not_cancel_the_token_is_an_optimizer_exception()
    {
        var reflect = new FakeChatClient().EnqueueError(new TaskCanceledException("timed out"));

        await Should.ThrowAsync<OptimizerException>(() =>
            Optimizer(new World(), reflect, Opts(iterations: 1), Val("v", 1))
                .CompileAsync(new Prog("first"), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct));
    }

    [Fact]
    public async Task A_nan_score_counts_as_zero_and_does_not_break_the_parent_draw()
    {
        var reflect = new FakeChatClient().EnqueueText("a").EnqueueText("b");

        var result = await Optimizer(new World(), reflect, Opts(iterations: 2, merge: false), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 2), new NanMetric(), TestHelpers.MakeContext(), ct: Ct);

        result.Score.ShouldBe(0.0);
        reflect.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_child_that_only_ties_its_parent_on_the_minibatch_is_never_run_on_the_validation_set()
    {
        var world = new World { Correct = (p, ex) => true };
        var reflect = new FakeChatClient().EnqueueText("same score");
        var reports = new List<OptimizerProgress>();

        await Optimizer(world, reflect, Opts(iterations: 1), Val("v", 4))
            .CompileAsync(new Prog("first"), Samples("t", 3), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        world.Calls.Count(c => c.StartsWith("v")).ShouldBe(4);
        reports.Last().Extras["Candidates"].ShouldBe(1);
    }

    [Fact]
    public async Task The_child_runs_on_the_same_minibatch_examples_as_its_parent()
    {
        var world = new World { Correct = (p, ex) => false };
        var reflect = new FakeChatClient().EnqueueText("x");

        await Optimizer(world, reflect, Opts(iterations: 1, minibatch: 2, merge: false), Val("v", 2))
            .CompileAsync(new Prog("first"), Samples("t", 6), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        // Two validation runs, then the parent on the minibatch, then the child on it.
        var names = world.Calls.Select(c => c.Split('|')[0]).ToList();
        names.Count.ShouldBe(6);
        names[2..4].ShouldAllBe(n => n.StartsWith("t"));
        names[4..6].ShouldBe(names[2..4]);
        names[2..4].Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_candidate_is_never_merged_with_its_own_ancestor()
    {
        // Only "first" set to X helps, and only on f examples. The unchanged program ties on the s examples,
        // so both it and its child stay on the frontier, but one is the ancestor of the other.
        var world = new World { Correct = (p, ex) => Q(ex)[1] == 'f' && p.Instruction("first") == "X" };
        var reflect = new FakeChatClient().EnqueueText("X").EnqueueText("Y").EnqueueText("Z");
        var reports = new List<OptimizerProgress>();

        await Optimizer(world, reflect, Opts(iterations: 3, minibatch: 4, mergeEvery: 3), Val("v", 4, "fs"))
            .CompileAsync(new Prog("first", "second"), Samples("t", 4, "fs"), new Exact(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reports.Last().Extras["FrontierSize"].ShouldBe(2);
        reports.Last().Extras["Candidates"].ShouldBe(2);
        reports.Last().Extras["Merges"].ShouldBe(0);
        // The third iteration was a merge turn with nothing to merge, so it mutated.
        reflect.RecordedCalls.Count.ShouldBe(3);
    }

    // ---- programs without named submodules --------------------------------------------------

    [Fact]
    public async Task A_program_with_no_named_submodule_is_tuned_under_one_shared_name()
    {
        var reflect = new FakeChatClient().EnqueueText("better");
        var sut = new GEPAOptimizer<PlainProg>(
            (p, ex, c, t) => Task.FromResult(new Example(ex.Inputs,
                new Dictionary<string, object?> { ["a"] = p.Ctx!.InstructionOverrides is { Count: > 0 } ? ex.Outputs["a"] : "wrong" })),
            reflect, Opts(iterations: 1), Val("v", 2));

        var result = await sut.CompileAsync(new PlainProg(), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        result.InstructionOverrides.Keys.ShouldBe(["__single__"]);
        reflect.RecordedCalls.Single().Messages.Last().Text.ShouldContain("Module: __single__");
    }

    [Fact]
    public async Task A_composed_program_without_submodules_is_tuned_under_one_shared_name()
    {
        var reflect = new FakeChatClient().EnqueueText("better");
        var sut = new GEPAOptimizer<EmptyProg>(
            (p, ex, c, t) => Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?> { ["a"] = "wrong" })),
            reflect, Opts(iterations: 1), Val("v", 1));

        var result = await sut.CompileAsync(new EmptyProg(), Samples("t", 2), new Exact(), TestHelpers.MakeContext(), ct: Ct);

        result.InstructionOverrides.ShouldBeEmpty();
        reflect.RecordedCalls.Single().Messages.Last().Text.ShouldContain("Module: __single__");
    }

    // ---- helpers ----------------------------------------------------------------------------

    static GEPAOptimizer<Prog> Optimizer(World world, IChatClient reflect, GepaOptions options, IReadOnlyList<Example> valset) =>
        new(world.RunAsync, reflect, options, valset);

    static GepaOptions Opts(
        int iterations, int minibatch = 2, int calls = 10_000, int seed = 0, bool merge = true, int mergeEvery = 3) =>
        new()
        {
            MaxIterations = iterations, MinibatchSize = minibatch, MaxMetricCalls = calls, Seed = seed,
            UseMerge = merge, MergeEvery = mergeEvery,
        };

    static string Q(Example ex) => (string)ex.Inputs["q"]!;

    static Example Sample(string name) =>
        new(new Dictionary<string, object?> { ["q"] = name }, new Dictionary<string, object?> { ["a"] = name });

    // Names are prefix + index, or prefix + kind letter + index when kinds are given ("fs" alternates f and s).
    static List<Example> Samples(string prefix, int count, string? kinds = null) =>
        Enumerable.Range(0, count)
            .Select(i => Sample(kinds is null ? $"{prefix}{i}" : $"{prefix}{kinds[i % kinds.Length]}{i}"))
            .ToList();

    static List<Example> Val(string prefix, int count, string? kinds = null) => Samples(prefix, count, kinds);

    // Examples of kind f need submodule "first" to read X and kind s need "second" to read Y.
    // With interference, a program that has both changed answers everything wrong.
    static Func<Prog, Example, bool> XyWorld(bool interference = false) => (p, ex) =>
    {
        var first = p.Instruction("first");
        var second = p.Instruction("second");
        if (interference && first == "X" && second == "Y") return false;
        return Q(ex)[1] == 'f' ? first == "X" : second == "Y";
    };

    static GEPAOptimizer<Prog>.Candidate Cand(
        int id, int[] parents, double[] scores, params (string Name, string Text)[] instructions) =>
        new(id, instructions.ToDictionary(i => i.Name, i => i.Text), parents, scores);

    sealed class World
    {
        public List<string> Calls { get; } = [];
        public Func<Prog, Example, bool> Correct { get; init; } = (_, _) => true;
        public Func<Prog, Example, Exception?>? Fault { get; init; }

        public Task<Example> RunAsync(Prog p, Example ex, AmpelumContext ctx, CancellationToken ct)
        {
            Calls.Add($"{Q(ex)}|{p.Instruction("first")}|{p.Instruction("second")}");
            if (Fault?.Invoke(p, ex) is { } fault) throw fault;
            var answer = Correct(p, ex) ? ex.Outputs["a"] : "wrong";
            return Task.FromResult(new Example(ex.Inputs, new Dictionary<string, object?> { ["a"] = answer }));
        }
    }

    sealed class Sub(AmpelumContext? ctx = null)
    {
        public AmpelumContext? Ctx { get; } = ctx;
        public Sub WithContext(AmpelumContext c) => new(c);
    }

    sealed class Prog(IReadOnlyDictionary<string, Sub> subs) : IComposedModule
    {
        public Prog(params string[] names) : this(names.ToDictionary(n => n, _ => new Sub())) { }

        public IReadOnlyDictionary<string, object> Submodules =>
            subs.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        public object Rebind(IReadOnlyDictionary<string, object> s) =>
            new Prog(s.ToDictionary(kv => kv.Key, kv => (Sub)kv.Value));

        public string? Instruction(string name) =>
            subs.TryGetValue(name, out var sub) ? sub.Ctx?.InstructionOverrides?.GetValueOrDefault(name) : null;
    }

    sealed class SigProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["qa"] = Predict.Create<QASig>(TestHelpers.MakeContext().Chat) };
        public object Rebind(IReadOnlyDictionary<string, object> s) => this;
    }

    sealed class PlainProg(AmpelumContext? ctx = null)
    {
        public AmpelumContext? Ctx { get; } = ctx;
        public PlainProg WithContext(AmpelumContext c) => new(c);
    }

    sealed class EmptyProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules => new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> s) => this;
    }

    sealed class Exact : IMetric
    {
        public int Calls { get; private set; }

        public double Score(object actual, object expected, SignatureMetadata signature)
        {
            Calls++;
            return Equals(((IReadOnlyDictionary<string, object?>)actual)["a"],
                          ((IReadOnlyDictionary<string, object?>)expected)["a"]) ? 1.0 : 0.0;
        }
    }

    sealed class Feedback : IMetric, IFeedbackMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            ScoreWithFeedback(actual, expected, signature).Score;

        public FeedbackScore ScoreWithFeedback(object actual, object expected, SignatureMetadata signature)
        {
            var got = ((IReadOnlyDictionary<string, object?>)actual)["a"];
            var want = ((IReadOnlyDictionary<string, object?>)expected)["a"];
            return Equals(got, want)
                ? new FeedbackScore(1.0, "right")
                : new FeedbackScore(0.0, $"the answer was wrong, wanted {want}");
        }
    }

    sealed class NanMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => double.NaN;
    }

    sealed class CancellingClient(CancellationTokenSource cts) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    sealed class ThrowingMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            throw new InvalidOperationException("metric failed");
    }

    sealed class SpyHook : IHook
    {
        public ConcurrentQueue<string> Seen { get; } = new();
        public List<string> Modules => [.. Seen];
        public int Order => 0;

        public ValueTask OnBeforeAsync(HookContext ctx, CancellationToken ct)
        {
            Seen.Enqueue(ctx.ModuleName);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnAfterAsync(HookContext ctx, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask OnErrorAsync(HookContext ctx, Exception error, CancellationToken ct) => ValueTask.CompletedTask;
    }

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    {
        public void Report(T value) => cb(value);
    }
}
