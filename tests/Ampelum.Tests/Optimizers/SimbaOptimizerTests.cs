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

public sealed class SimbaOptimizerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The first strategy draws of these seeds: 3 is rule then demo, 0 is demo, 4 is rule then rule.
    const int RuleThenDemo = 3;
    const int Demo = 0;
    const int Rule = 4;

    const string UnknownInstruction = "(the module's own instruction)";

    // ---- the scripted world ------------------------------------------------------------------
    // The program is a module that remembers the context it is bound to. Each run reports the state it
    // ran in and gets the score a script gives it, so tests decide exactly which runs are good.

    sealed class Mod(AmpelumContext? ctx = null)
    {
        public Mod WithContext(AmpelumContext c) => new(c);
        public string Instruction => ctx?.InstructionOverrides?.Values.FirstOrDefault() ?? "";
        public IReadOnlyList<Example> Demos => ctx?.DefaultDemos ?? [];
    }

    sealed record Seen(string Question, string Instruction, IReadOnlyList<Example> Demos);

    sealed class World
    {
        public List<Seen> Calls { get; } = [];
        public Func<int, Mod, Example, double> Score { get; init; } = (_, _, _) => 0.0;
        public Func<int, Exception?>? Fault { get; init; }

        public Task<Example> RunAsync(Mod mod, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            int call = Calls.Count;
            Calls.Add(new Seen(Q(example), mod.Instruction, mod.Demos));
            if (Fault?.Invoke(call) is { } fault) throw fault;
            return Task.FromResult(new Example(
                example.Inputs,
                new Dictionary<string, object?> { ["a"] = $"ans-{call}", ["score"] = Score(call, mod, example) }));
        }
    }

    // The score is whatever the run reported.
    sealed class Reported : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            (double)((IReadOnlyDictionary<string, object?>)actual)["score"]!;
    }

    sealed class ThrowingMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) =>
            throw new InvalidOperationException("metric failed");
    }

    static string Q(Example example) => (string)example.Inputs["q"]!;

    static IReadOnlyList<Example> Samples(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Example(
            new Dictionary<string, object?> { ["q"] = $"{prefix}{i}" },
            new Dictionary<string, object?> { ["a"] = $"a{i}" }))];

    static SimbaOptions Opts(int seed, int steps = 1, int minibatch = 4, int perStep = 2, int maxDemos = 2, double temperature = 1.0) =>
        new()
        {
            Seed = seed, MaxSteps = steps, MinibatchSize = minibatch, CandidatesPerStep = perStep,
            MaxDemos = maxDemos, Temperature = temperature,
        };

    // Scores of the two runs of the baseline in the first step: four examples, two slots.
    static readonly double[] SlotZero = [1.0, 0.5, 0.0, 0.6];
    static readonly double[] SlotOne = [0.0, 0.5, 0.9, 0.0];

    // Calls 0..7 follow the script; later calls score by what the program carries.
    static double Scripted(int call, Mod mod, Example example) =>
        call < 4 ? SlotZero[call]
        : call < 8 ? SlotOne[call - 4]
        : mod.Instruction.Contains("RULE", StringComparison.Ordinal) ? 1.0
        : mod.Demos.Count > 0 ? 0.2
        : 0.1;

    sealed class SpyHook : IHook
    {
        public ConcurrentQueue<string> Seen { get; } = new();
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

    static SimbaOptimizer<Mod> Optimizer(
        World world, IChatClient reflect, SimbaOptions options, IReadOnlyList<Example>? valset = null) =>
        new(world.RunAsync, reflect, options, valset);

    // ---- constructor and arguments -----------------------------------------------------------

    [Fact]
    public void Ctor_NullRunner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new SimbaOptimizer<Mod>(null!, new FakeChatClient()));

    [Fact]
    public void Ctor_NullReflectionClient_Throws() =>
        Should.Throw<ArgumentNullException>(() => new SimbaOptimizer<Mod>(new World().RunAsync, null!));

    [Theory]
    [InlineData(0, 1, 1, 1, 1.0)]
    [InlineData(1, 0, 1, 1, 1.0)]
    [InlineData(1, 1, 0, 1, 1.0)]
    [InlineData(1, 1, 1, 0, 1.0)]
    [InlineData(1, 1, 1, 1, 0.0)]
    [InlineData(1, 1, 1, 1, -0.5)]
    public void Ctor_OutOfRangeOption_Throws(int minibatch, int perStep, int steps, int demos, double temperature) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SimbaOptimizer<Mod>(
            new World().RunAsync, new FakeChatClient(),
            new SimbaOptions
            {
                MinibatchSize = minibatch, CandidatesPerStep = perStep, MaxSteps = steps,
                MaxDemos = demos, Temperature = temperature,
            }));

    [Fact]
    public void Ctor_EmptyValset_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => new SimbaOptimizer<Mod>(
            new World().RunAsync, new FakeChatClient(), valset: []));
        ex.ParamName.ShouldBe("valset");
    }

    [Fact]
    public void Options_have_documented_defaults()
    {
        var d = new SimbaOptions();
        d.MinibatchSize.ShouldBe(8);
        d.CandidatesPerStep.ShouldBe(4);
        d.MaxSteps.ShouldBe(6);
        d.MaxDemos.ShouldBe(4);
        d.Temperature.ShouldBe(0.2);
        d.Seed.ShouldBe(0);

        new SimbaOptimizer<Mod>(new World().RunAsync, new FakeChatClient()).Options.ShouldBe(d);
        var custom = new SimbaOptions { Seed = 7 };
        new SimbaOptimizer<Mod>(new World().RunAsync, new FakeChatClient(), custom).Options.ShouldBeSameAs(custom);
    }

    [Fact]
    public async Task CompileAsync_NullArguments_Throw()
    {
        var sut = Optimizer(new World(), new FakeChatClient(), Opts(Demo));
        var ctx = TestHelpers.MakeContext();
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(null!, Samples("t", 1), new Reported(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Mod(), null!, new Reported(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Mod(), Samples("t", 1), null!, ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Mod(), Samples("t", 1), new Reported(), null!, ct: Ct));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_Throws()
    {
        var sut = Optimizer(new World(), new FakeChatClient(), Opts(Demo));
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            sut.CompileAsync(new Mod(), [], new Reported(), TestHelpers.MakeContext(), ct: Ct));
        ex.ParamName.ShouldBe("trainset");
    }

    // ---- the algorithm steps -----------------------------------------------------------------

    [Fact]
    public void Softmax_weights_follow_the_scores_and_the_temperature()
    {
        var warm = SimbaOptimizer<Mod>.SoftmaxWeights([1.0, 0.5, 0.0], 0.5);
        warm[0].ShouldBe(1.0);
        warm[1].ShouldBe(Math.Exp(-1), 1e-12);
        warm[2].ShouldBe(Math.Exp(-2), 1e-12);

        var cold = SimbaOptimizer<Mod>.SoftmaxWeights([1.0, 0.5, 0.0], 0.1);
        cold[1].ShouldBeLessThan(warm[1]);
        cold[1].ShouldBe(Math.Exp(-5), 1e-12);
    }

    [Fact]
    public void Draws_follow_the_weights()
    {
        var rng = new Random(1);
        var weights = new[] { 1.0, 3.0 };
        int second = Enumerable.Range(0, 4000).Count(_ => SimbaOptimizer<Mod>.PickIndex(weights, rng) == 1);

        second.ShouldBeInRange(2900, 3100);
    }

    [Fact]
    public void A_weight_of_zero_is_never_drawn()
    {
        var rng = new Random(2);
        Enumerable.Range(0, 200).All(_ => SimbaOptimizer<Mod>.PickIndex([0.0, 1.0], rng) == 1).ShouldBeTrue();
        Enumerable.Range(0, 200).All(_ => SimbaOptimizer<Mod>.PickIndex([1.0, 0.0], rng) == 0).ShouldBeTrue();
    }

    [Fact]
    public void Examples_are_ranked_by_spread_largest_first_and_a_spread_of_zero_is_left_out()
    {
        // Spreads: 1.0, 0, 0.7, 0.
        double[][] scores = [[1.0, 0.5, 0.2, 0.4], [0.0, 0.5, 0.9, 0.4]];

        SimbaOptimizer<Mod>.RankBySpread(scores).ShouldBe([0, 2]);
    }

    [Fact]
    public void Equal_spreads_keep_the_example_order()
    {
        double[][] scores = [[1.0, 1.0, 0.0, 0.5], [0.0, 0.0, 0.0, 0.0]];

        SimbaOptimizer<Mod>.RankBySpread(scores).ShouldBe([0, 1, 3]);
    }

    [Fact]
    public void A_demo_is_added_last_and_the_oldest_go_beyond_the_cap()
    {
        var one = Samples("d", 3);
        var other = Samples("o", 1);
        var start = new Dictionary<string, IReadOnlyList<Example>> { ["s"] = [one[0], one[1]], ["t"] = other };

        var result = SimbaOptimizer<Mod>.AppendDemo(start, "s", one[2], 2);

        result["s"].ShouldBe([one[1], one[2]]);
        result["t"].ShouldBe(other);
        start["s"].Count.ShouldBe(2);
    }

    [Fact]
    public void A_demo_for_a_submodule_without_demos_starts_its_list()
    {
        var demo = Samples("d", 1)[0];

        var result = SimbaOptimizer<Mod>.AppendDemo(new Dictionary<string, IReadOnlyList<Example>>(), "s", demo, 4);

        result["s"].ShouldBe([demo]);
    }

    [Fact]
    public void A_rule_goes_on_a_new_line_after_the_current_instruction()
    {
        var start = new Dictionary<string, string> { ["s"] = "old", ["t"] = "keep" };

        var result = SimbaOptimizer<Mod>.AppendRule(start, "s", "current text", "the rule");

        result["s"].ShouldBe("current text\nthe rule");
        result["t"].ShouldBe("keep");
        start["s"].ShouldBe("old");
    }

    // ---- a full step -------------------------------------------------------------------------

    [Fact]
    public async Task One_step_builds_a_rule_candidate_and_a_demo_candidate_from_the_two_widest_spreads()
    {
        var world = new World { Score = Scripted };
        var reflect = new TextModel("RULE one");
        var spy = new SpyHook();
        var ctx = TestHelpers.MakeContext() with { Hooks = [spy] };

        var result = await Optimizer(world, reflect, Opts(RuleThenDemo))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), ctx, ct: Ct);

        // Slot 0 ran calls 0..3 and slot 1 calls 4..7 on the same minibatch, in the same order.
        var minibatch = world.Calls.Take(4).Select(c => c.Question).ToList();
        world.Calls.Skip(4).Take(4).Select(c => c.Question).ShouldBe(minibatch);
        minibatch.Distinct().Count().ShouldBe(4);

        // Spreads 1.0, 0, 0.9, 0.6: the two widest are examples 0 and 2. Example 3 (0.6) gets no candidate.
        var prompt = Kit.UserText(reflect.Calls.Single());
        prompt.ShouldContain("Module: __single__");
        prompt.ShouldContain($"Inputs: q={minibatch[0]}");
        prompt.ShouldContain("Best run, score 1:\nOutputs: a=ans-0, score=1");
        prompt.ShouldContain("Worst run, score 0:\nOutputs: a=ans-4, score=0");
        prompt.ShouldNotContain(minibatch[3]);
        prompt.ShouldNotContain(minibatch[2]);
        spy.Seen.ToArray().ShouldBe(["SIMBA.Rule"]);

        // The rule candidate (calls 8..11) carries the rule; the demo candidate (calls 12..15) the demo of
        // example 2: its inputs and the outputs of its best run, which is slot 1, call 6.
        world.Calls.Skip(8).Take(4).ShouldAllBe(c => c.Instruction == $"{UnknownInstruction}\nRULE one" && c.Demos.Count == 0);
        world.Calls.Skip(12).Take(4).ShouldAllBe(c => c.Instruction == "" && c.Demos.Count == 1);
        var demo = world.Calls[12].Demos.Single();
        demo.Inputs["q"].ShouldBe(minibatch[2]);
        demo.Outputs["a"].ShouldBe("ans-6");

        // Finalists in order of minibatch mean: rule candidate, baseline, demo candidate; each on all 4 examples.
        world.Calls.Count.ShouldBe(28);
        result.Score.ShouldBe(1.0);
        result.InstructionOverrides["__single__"].ShouldBe($"{UnknownInstruction}\nRULE one");
        result.DemosBySubmodule.ShouldBeEmpty();
        result.TrainsetSize.ShouldBe(4);
    }

    [Fact]
    public async Task The_demo_strategy_does_not_call_the_reflection_model()
    {
        var world = new World { Score = Scripted };
        var reflect = new TextModel("RULE one");

        // Seed 0 draws demo for the first candidate; the second also draws demo.
        var result = await Optimizer(world, reflect, Opts(Demo))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        reflect.Calls.ShouldBeEmpty();
        // Two demo candidates, each scoring 0.2 on the full set; the baseline scores 0.1.
        result.Score.ShouldBe(0.2);
        result.DemosBySubmodule["__single__"].Count.ShouldBe(1);
        result.InstructionOverrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_best_by_full_evaluation_is_returned_not_the_best_by_minibatch()
    {
        // The rule candidate wins every minibatch (the training questions start with t) and fails on the
        // validation questions (they start with v). The demo candidate scores 0.5 everywhere.
        double Overfit(int call, Mod mod, Example example) =>
            call < 8 ? Scripted(call, mod, example)
            : mod.Instruction.Contains("RULE", StringComparison.Ordinal) ? (Q(example).StartsWith('v') ? 0.0 : 1.0)
            : mod.Demos.Count > 0 ? 0.5
            : 0.1;
        var world = new World { Score = Overfit };

        var result = await Optimizer(world, new TextModel("RULE one"), Opts(RuleThenDemo), Samples("v", 2))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        // 16 calls in the step, then 3 finalists on the 2 validation examples.
        world.Calls.Count.ShouldBe(22);
        world.Calls.Skip(16).ShouldAllBe(c => c.Question.StartsWith('v'));
        result.Score.ShouldBe(0.5);
        result.DemosBySubmodule["__single__"].Count.ShouldBe(1);
        result.InstructionOverrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_next_step_draws_its_programs_from_the_pool_by_score()
    {
        var world = new World { Score = Scripted };

        await Optimizer(world, new TextModel("RULE one"), Opts(RuleThenDemo, steps: 2, temperature: 0.01))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        // After step 1 the rule candidate has the best minibatch mean (1.0 against 0.4375 and 0.2). At a
        // temperature of 0.01 both programs of step 2 are that candidate: calls 16..23.
        world.Calls.Skip(16).Take(8).ShouldAllBe(c => c.Instruction.Contains("RULE") && c.Demos.Count == 0);
    }

    [Fact]
    public async Task A_step_without_any_spread_adds_no_candidate_and_asks_nobody()
    {
        var world = new World { Score = (_, _, _) => 0.5 };
        var reflect = new TextModel("RULE one");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(Rule, steps: 2))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reflect.Calls.ShouldBeEmpty();
        result.Score.ShouldBe(0.5);
        result.InstructionOverrides.ShouldBeEmpty();
        result.DemosBySubmodule.ShouldBeEmpty();
        // Per step: two programs on four examples; then the single finalist on four.
        world.Calls.Count.ShouldBe(2 * 8 + 4);
        reports.Select(r => r.Phase).ShouldBe(["step", "step", "evaluate"]);
        reports.Select(r => r.Step).ShouldBe([1, 2, 2]);
        reports.ShouldAllBe(r => r.TotalSteps == 2);
        reports.ShouldAllBe(r => (int)r.Extras["Candidates"]! == 1 && (int)r.Extras["RunnerErrors"]! == 0);
        reports.Select(r => r.BestScore).ShouldBe([0.5, 0.5, 0.5]);
    }

    [Fact]
    public async Task Progress_reports_the_pool_size_and_the_best_minibatch_mean()
    {
        var world = new World { Score = Scripted };
        var reports = new List<OptimizerProgress>();

        await Optimizer(world, new TextModel("RULE one"), Opts(RuleThenDemo))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reports.Select(r => r.Phase).ShouldBe(["step", "evaluate"]);
        reports[0].Extras["Candidates"].ShouldBe(3);
        reports[0].BestScore.ShouldBe(1.0);
        reports[1].BestScore.ShouldBe(1.0);
    }

    [Fact]
    public async Task The_reflection_call_goes_to_the_reflection_client_not_to_the_context_client()
    {
        var world = new World { Score = Scripted };
        var reflect = new TextModel("RULE one");
        var contextClient = new FakeChatClient();

        await Optimizer(world, reflect, Opts(Rule))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(),
                TestHelpers.MakeContext(contextClient), ct: Ct);

        reflect.Calls.Count.ShouldBe(2);
        contextClient.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_rule_adds_no_candidate()
    {
        var world = new World { Score = Scripted };
        var reflect = new TextModel("   ");
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, reflect, Opts(Rule))
            .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reflect.Calls.Count.ShouldBe(2);
        reports[0].Extras["Candidates"].ShouldBe(1);
        result.InstructionOverrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_run()
    {
        async Task<(World, TextModel, OptimizedProgram<Mod>)> RunOnce()
        {
            var world = new World { Score = Scripted };
            var reflect = new TextModel("RULE one");
            var result = await Optimizer(world, reflect, Opts(RuleThenDemo, steps: 2))
                .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);
            return (world, reflect, result);
        }

        var (worldA, reflectA, a) = await RunOnce();
        var (worldB, reflectB, b) = await RunOnce();

        worldB.Calls.Select(c => (c.Question, c.Instruction, c.Demos.Count))
            .ShouldBe(worldA.Calls.Select(c => (c.Question, c.Instruction, c.Demos.Count)));
        reflectB.Calls.Select(Kit.UserText).ShouldBe(reflectA.Calls.Select(Kit.UserText));
        b.Score.ShouldBe(a.Score);
        b.InstructionOverrides.ShouldBe(a.InstructionOverrides);
    }

    [Fact]
    public async Task A_different_seed_draws_a_different_minibatch()
    {
        async Task<List<string>> FirstFour(int seed)
        {
            var world = new World { Score = (_, _, _) => 0.5 };
            await Optimizer(world, new TextModel("x"), Opts(seed, minibatch: 4, perStep: 1))
                .CompileAsync(new Mod(), Samples("t", 12), new Reported(), TestHelpers.MakeContext(), ct: Ct);
            return [.. world.Calls.Take(4).Select(c => c.Question)];
        }

        (await FirstFour(1)).ShouldNotBe(await FirstFour(2));
    }

    // ---- errors -------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_run_scores_zero_is_counted_and_shows_in_the_prompt_as_the_worst_run()
    {
        // Call 0 fails; everything else scores 1. Example 0 then has spread 1 (best: slot 1, worst: the failed run).
        var world = new World { Score = (_, _, _) => 1.0, Fault = call => call == 0 ? new InvalidOperationException("boom") : null };
        var reflect = new TextModel("RULE one");
        var reports = new List<OptimizerProgress>();
        var stopped = new ConcurrentBag<Activity>();
        using var source = new ActivitySource("SimbaOptimizerTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("run");

        var result = await Optimizer(world, reflect, Opts(Rule, minibatch: 2))
            .CompileAsync(new Mod(), Samples("t", 2), new Reported(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        var prompt = Kit.UserText(reflect.Calls.Single());
        prompt.ShouldContain("Best run, score 1:");
        prompt.ShouldContain("Worst run, score 0:\nOutputs: (the run failed: boom)");
        reports.Last().Extras["RunnerErrors"].ShouldBe(1);
        var mine = stopped.Single(a => a.Parent == parent && (string?)a.GetTagItem("optimizer.type") == "SimbaOptimizer");
        mine.GetTagItem("optimizer.runner_errors").ShouldBe(1);
        mine.GetTagItem("optimizer.final_score").ShouldBe(result.Score);
    }

    [Fact]
    public async Task A_best_run_that_failed_has_no_outputs_so_it_yields_no_demo()
    {
        // The metric scores a good run -1, so the failed run (0) is the best of two.
        var world = new World { Score = (_, _, _) => -1.0, Fault = call => call == 0 ? new InvalidOperationException("boom") : null };
        var reports = new List<OptimizerProgress>();

        await Optimizer(world, new TextModel("RULE one"), Opts(Demo, minibatch: 1))
            .CompileAsync(new Mod(), Samples("t", 1), new Reported(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        reports[0].Extras["Candidates"].ShouldBe(1);
    }

    [Fact]
    public async Task A_metric_exception_is_counted_like_a_runner_exception()
    {
        var world = new World();
        var reports = new List<OptimizerProgress>();

        var result = await Optimizer(world, new TextModel("x"), Opts(Demo, minibatch: 2, perStep: 1))
            .CompileAsync(new Mod(), Samples("t", 2), new ThrowingMetric(), TestHelpers.MakeContext(),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.Score.ShouldBe(0.0);
        // One program on two examples in the step, then the finalist on two.
        reports.Last().Extras["RunnerErrors"].ShouldBe(4);
    }

    [Fact]
    public async Task A_NaN_score_counts_as_zero()
    {
        var world = new World { Score = (_, _, _) => double.NaN };

        var result = await Optimizer(world, new TextModel("x"), Opts(Demo, minibatch: 2, perStep: 1))
            .CompileAsync(new Mod(), Samples("t", 2), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        result.Score.ShouldBe(0.0);
    }

    [Fact]
    public async Task A_failing_reflection_call_throws_OptimizerException()
    {
        var world = new World { Score = Scripted };

        var ex = await Should.ThrowAsync<OptimizerException>(() =>
            Optimizer(world, new FakeChatClient(), Opts(Rule))
                .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct));

        ex.Message.ShouldContain("SIMBA reflection failed for submodule '__single__'");
        ex.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancellation_before_the_start_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(new World(), new TextModel("x"), Opts(Demo))
                .CompileAsync(new Mod(), Samples("t", 2), new Reported(), TestHelpers.MakeContext(), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_inside_the_runner_propagates_and_is_not_counted_as_an_error()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = new World
        {
            Fault = _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            },
        };
        var reports = new List<OptimizerProgress>();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(world, new TextModel("x"), Opts(Demo))
                .CompileAsync(new Mod(), Samples("t", 2), new Reported(), TestHelpers.MakeContext(),
                    new SyncProgress<OptimizerProgress>(reports.Add), cts.Token));

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_inside_the_reflection_call_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = new World { Score = Scripted };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(world, new CancellingClient(cts), Opts(Rule))
                .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_between_candidates_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var world = new World { Score = Scripted };
        var reflect = new ScriptedReflect(() => cts.Cancel());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(world, reflect, Opts(Rule))
                .CompileAsync(new Mod(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: cts.Token));

        // The first rule was asked for; the second was not.
        reflect.Calls.ShouldBe(1);
    }

    // Answers a rule, and runs a callback while answering.
    sealed class ScriptedReflect(Action onCall) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            onCall();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "RULE one")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // ---- submodules ----------------------------------------------------------------------------

    sealed class Pair(IReadOnlyDictionary<string, Mod> mods) : IComposedModule
    {
        public Pair() : this(new Dictionary<string, Mod> { ["first"] = new(), ["second"] = new() })
        {
        }

        public IReadOnlyDictionary<string, object> Submodules =>
            mods.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

        public object Rebind(IReadOnlyDictionary<string, object> s) =>
            new Pair(s.ToDictionary(kv => kv.Key, kv => (Mod)kv.Value));
    }

    [Fact]
    public async Task The_submodule_that_gets_a_candidate_is_picked_round_robin()
    {
        // Two rule candidates in one step: the first goes to "first", the second to "second".
        var seen = new List<IReadOnlyDictionary<string, string>>();
        Task<Example> Run(Pair pair, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            int call = seen.Count;
            seen.Add(pair.Submodules.ToDictionary(
                kv => kv.Key, kv => ((Mod)kv.Value).Instruction));
            return Task.FromResult(new Example(
                example.Inputs,
                new Dictionary<string, object?> { ["a"] = $"ans-{call}", ["score"] = Scripted(call, new Mod(), example) }));
        }
        var reflect = new TextModel("RULE one");

        var result = await new SimbaOptimizer<Pair>(Run, reflect, Opts(Rule))
            .CompileAsync(new Pair(), Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        reflect.Calls.Select(Kit.UserText).First().ShouldContain("Module: first");
        reflect.Calls.Select(Kit.UserText).Last().ShouldContain("Module: second");
        // Calls 8..11 ran the first candidate and 12..15 the second.
        seen[8]["first"].ShouldContain("RULE one");
        seen[8]["second"].ShouldBe("");
        seen[12]["first"].ShouldBe("");
        seen[12]["second"].ShouldContain("RULE one");
        result.InstructionOverrides.Keys.ShouldAllBe(k => k == "first" || k == "second");
    }

    // ---- against a real Predict ------------------------------------------------------------------

    [Fact]
    public async Task A_rule_and_a_demo_reach_the_prompt_of_a_real_predict_and_the_rule_wins()
    {
        const string RuleText = "Always answer with the digit after the q.";
        var seenCount = new Dictionary<string, int>();
        var gate = new object();
        var model = new ScriptedModel(messages =>
        {
            var question = Enumerable.Range(0, 4).Select(i => $"q{i}").Single(q => Kit.UserText(messages).Contains(q));
            var right = $"a{question[1]}";
            if (Kit.SystemText(messages).Contains(RuleText, StringComparison.Ordinal)) return right;
            // Under demos the model knows two questions.
            if (messages.Count > 2) return question is "q0" or "q1" ? right : "wrong";
            // Without either, it answers right the first time it sees a question and wrong every time after.
            lock (gate)
            {
                seenCount[question] = seenCount.GetValueOrDefault(question) + 1;
                return seenCount[question] == 1 ? right : "wrong";
            }
        });
        var trainset = Enumerable.Range(0, 4).Select(i => Kit.Qa($"q{i}", $"a{i}")).ToList();

        var result = await new SimbaOptimizer<Predict<KitSig>>(
                Kit.RunAsync, new TextModel(RuleText), Opts(RuleThenDemo, minibatch: 4))
            .CompileAsync(Predict<KitSig>.FromAmbient(), trainset, new AnswerMatch(), Kit.Context(model), ct: Ct);

        // The rule is added to the signature's own instruction.
        result.InstructionOverrides["__single__"].ShouldBe($"Given a question, produce an answer.\n{RuleText}");
        result.Score.ShouldBe(1.0);
        model.Calls.Select(Kit.SystemText).ShouldContain(s => s.Contains(RuleText, StringComparison.Ordinal));

        // The demo: the second example of the minibatch, with the answer its best run gave.
        var demoQuestion = Enumerable.Range(0, 4).Select(i => $"q{i}")
            .Single(q => Kit.UserText(model.Calls[1]).Contains(q));
        var withDemo = model.Calls.Where(m => m.Count == 4).ToList();
        withDemo.ShouldNotBeEmpty();
        withDemo.ShouldAllBe(m => m[1].Text.Contains(demoQuestion) && m[2].Text.Contains($"a{demoQuestion[1]}"));
    }
}
