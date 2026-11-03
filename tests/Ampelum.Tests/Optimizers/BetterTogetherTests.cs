using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class BetterTogetherTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the scripted world ------------------------------------------------------------------
    // The program remembers the context it is bound to. Each step is a stub that returns a fixed
    // result, and the score of a bound program is a function of what it carries.

    static readonly ScriptedModel TunedClient = new(_ => "tuned");

    sealed class Prog(AmpelumContext? ctx = null)
    {
        public Prog WithContext(AmpelumContext c) => new(c);
        public string? Instruction => ctx?.InstructionOverrides?.Values.FirstOrDefault();
        public IReadOnlyList<Example> Demos => ctx?.DefaultDemos ?? [];
        public bool Tuned => ReferenceEquals(ctx?.Chat, TunedClient);

        public string Describe() => $"{Instruction}|{(Tuned ? "tuned" : "base")}|{Demos.Count}";
    }

    sealed class Stub(string name, List<string> log, Func<Prog, OptimizedProgram<Prog>> make) : IOptimizer<Prog>
    {
        public List<IReadOnlyList<Example>> Trainsets { get; } = [];

        public Task<OptimizedProgram<Prog>> CompileAsync(
            Prog program, IReadOnlyList<Example> trainset, IMetric metric, AmpelumContext context,
            IProgress<OptimizerProgress>? progress = null, CancellationToken ct = default)
        {
            log.Add($"{name}:{program.Describe()}");
            Trainsets.Add(trainset);
            return Task.FromResult(make(program));
        }
    }

    static OptimizedProgram<Prog> Result(
        Prog program,
        IReadOnlyDictionary<string, IReadOnlyList<Example>>? demos = null,
        IReadOnlyDictionary<string, string>? instructions = null,
        IReadOnlyDictionary<string, IChatClient>? clients = null) =>
        new(program, demos ?? new Dictionary<string, IReadOnlyList<Example>>(), instructions ?? new Dictionary<string, string>())
        {
            ChatClients = clients ?? new Dictionary<string, IChatClient>(),
        };

    static OptimizedProgram<Prog> Instruct(Prog program, string text) =>
        Result(program, instructions: new Dictionary<string, string> { ["__single__"] = text });

    static OptimizedProgram<Prog> Tune(Prog program) =>
        Result(program, clients: new Dictionary<string, IChatClient> { ["__single__"] = TunedClient });

    // P1 scores 0.4, P1 on the tuned client 0.9, P2 on the tuned client 0.6, anything else 0.1.
    static double Worth(Prog p) => (p.Instruction, p.Tuned) switch
    {
        ("P1", false) => 0.4,
        ("P1", true) => 0.9,
        ("P2", true) => 0.6,
        _ => 0.1,
    };

    sealed class World
    {
        public ConcurrentQueue<(string Question, string State)> Scored { get; } = new();
        public Func<Prog, double> Score { get; init; } = Worth;
        public Func<Example, Exception?>? Fault { get; init; }

        public Task<Example> RunAsync(Prog p, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            Scored.Enqueue((Q(example), p.Describe()));
            if (Fault?.Invoke(example) is { } fault) throw fault;
            return Task.FromResult(new Example(
                example.Inputs, new Dictionary<string, object?> { ["a"] = "x", ["score"] = Score(p) }));
        }
    }

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

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    {
        public void Report(T value) => cb(value);
    }

    static string Q(Example example) => (string)example.Inputs["q"]!;

    static List<Example> Samples(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Example(
            new Dictionary<string, object?> { ["q"] = $"{prefix}{i}" },
            new Dictionary<string, object?> { ["a"] = $"a{i}" }))];

    static BetterTogether<Prog> Together(
        World world, IOptimizer<Prog> prompt, IOptimizer<Prog>? weight = null, string strategy = "p -> w -> p",
        IReadOnlyList<Example>? valset = null, int seed = 0) =>
        new(world.RunAsync, prompt, weight, strategy: strategy, valset: valset ?? Samples("v", 2), seed: seed);

    static Task<OptimizedProgram<Prog>> Run(
        BetterTogether<Prog> optimizer, IProgress<OptimizerProgress>? progress = null, IReadOnlyList<Example>? trainset = null,
        IMetric? metric = null, CancellationToken? ct = null) =>
        optimizer.CompileAsync(
            new Prog(), trainset ?? Samples("t", 4), metric ?? new Reported(), TestHelpers.MakeContext(),
            progress, ct ?? Ct);

    // Step 1 sets P1, step 2 tunes the client, step 3 sets P2.
    static (Stub Prompt, Stub Weight, List<string> Log) PromptWeightPrompt()
    {
        var log = new List<string>();
        int promptCalls = 0;
        var prompt = new Stub("p", log, program => promptCalls++ == 0 ? Instruct(program, "P1") : Instruct(program, "P2"));
        var weight = new Stub("w", log, Tune);
        return (prompt, weight, log);
    }

    // ---- strategy -------------------------------------------------------------------------------

    [Fact]
    public void The_default_strategy_is_prompt_weight_prompt()
    {
        var sut = new BetterTogether<Prog>(new World().RunAsync, new Stub("p", [], p => Result(p)), new Stub("w", [], p => Result(p)));

        sut.Steps.ShouldBe(['p', 'w', 'p']);
    }

    [Theory]
    [InlineData("p", "p")]
    [InlineData("w", "w")]
    [InlineData("w -> p", "wp")]
    [InlineData("p -> p -> w -> w", "ppww")]
    public void A_strategy_is_split_into_its_steps(string strategy, string expected) =>
        BetterTogether<Prog>.ParseStrategy(strategy).ShouldBe([.. expected]);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("x")]
    [InlineData("P -> W")]
    [InlineData("p->w")]
    [InlineData("p  ->  w")]
    [InlineData("p -> ")]
    [InlineData("p ->")]
    [InlineData("p -> -> w")]
    [InlineData("p, w")]
    [InlineData("pw")]
    public void An_invalid_strategy_is_rejected(string strategy)
    {
        var ex = Should.Throw<ArgumentException>(() => new BetterTogether<Prog>(
            new World().RunAsync, new Stub("p", [], p => Result(p)), new Stub("w", [], p => Result(p)), strategy: strategy));
        ex.ParamName.ShouldBe("strategy");
    }

    [Fact]
    public void A_null_strategy_is_rejected() =>
        Should.Throw<ArgumentNullException>(() => new BetterTogether<Prog>(
            new World().RunAsync, new Stub("p", [], p => Result(p)), strategy: null!));

    // ---- constructor and arguments -----------------------------------------------------------------

    [Fact]
    public void Ctor_NullRunner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new BetterTogether<Prog>(null!));

    [Fact]
    public void Ctor_EmptyValset_Throws()
    {
        var ex = Should.Throw<ArgumentException>(() => new BetterTogether<Prog>(
            new World().RunAsync, strategy: "p", valset: []));
        ex.ParamName.ShouldBe("valset");
    }

    [Fact]
    public void A_weight_step_without_a_weight_optimizer_or_a_provider_is_rejected()
    {
        var ex = Should.Throw<ArgumentException>(() => new BetterTogether<Prog>(new World().RunAsync));
        ex.ParamName.ShouldBe("fineTuneProvider");
    }

    [Fact]
    public void A_prompt_only_strategy_needs_no_provider() =>
        new BetterTogether<Prog>(new World().RunAsync, strategy: "p -> p").Steps.ShouldBe(['p', 'p']);

    [Fact]
    public async Task CompileAsync_NullArguments_Throw()
    {
        var sut = Together(new World(), new Stub("p", [], p => Result(p)), strategy: "p");
        var ctx = TestHelpers.MakeContext();
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(null!, Samples("t", 1), new Reported(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog(), null!, new Reported(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog(), Samples("t", 1), null!, ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(new Prog(), Samples("t", 1), new Reported(), null!, ct: Ct));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_Throws()
    {
        var sut = Together(new World(), new Stub("p", [], p => Result(p)), strategy: "p");
        var ex = await Should.ThrowAsync<ArgumentException>(() => Run(sut, trainset: []));
        ex.ParamName.ShouldBe("trainset");
    }

    // ---- the steps ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_steps_run_in_order_and_each_compiles_the_bound_result_of_the_one_before()
    {
        var (prompt, weight, log) = PromptWeightPrompt();

        await Run(Together(new World(), prompt, weight));

        // Step 1 gets the program itself. Step 2 gets it bound with P1. Step 3 gets it with P1 and the tuned client.
        log.ShouldBe(["p:|base|0", "w:P1|base|0", "p:P1|tuned|0"]);
    }

    [Fact]
    public async Task The_best_step_is_returned_not_the_last_one()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var program = new Prog();

        var result = await Together(new World(), prompt, weight)
            .CompileAsync(program, Samples("t", 4), new Reported(), TestHelpers.MakeContext(), ct: Ct);

        // After the steps: P1 0.4, P1 and tuned 0.9, P2 and tuned 0.6.
        result.StepScores.ShouldBe([0.4, 0.9, 0.6]);
        result.Score.ShouldBe(0.9);
        result.InstructionOverrides["__single__"].ShouldBe("P1");
        result.ChatClients["__single__"].ShouldBeSameAs(TunedClient);
        result.Program.ShouldBeSameAs(program);
        result.TrainsetSize.ShouldBe(4);
    }

    [Fact]
    public async Task A_later_step_replaces_the_earlier_output_per_submodule_and_keeps_the_rest()
    {
        var log = new List<string>();
        var d1 = Samples("d", 2);
        var first = new Stub("p", log, p => Result(p,
            new Dictionary<string, IReadOnlyList<Example>> { ["x"] = [d1[0]], ["y"] = [d1[1]] },
            new Dictionary<string, string> { ["x"] = "I1", ["y"] = "J1" },
            new Dictionary<string, IChatClient> { ["x"] = new ScriptedModel(_ => "first") }));
        var tunedX = new ScriptedModel(_ => "second");
        var second = new Stub("w", log, p => Result(p,
            new Dictionary<string, IReadOnlyList<Example>> { ["x"] = [d1[1], d1[0]] },
            new Dictionary<string, string> { ["x"] = "I2" },
            new Dictionary<string, IChatClient> { ["x"] = tunedX }));

        var result = await Run(Together(new World { Score = _ => 0.5 }, first, second, strategy: "p -> w"));

        // Equal scores: the earliest step wins, so the first step's own output is the result.
        result.InstructionOverrides.ShouldBe(new Dictionary<string, string> { ["x"] = "I1", ["y"] = "J1" });

        var better = await Run(Together(
            new World { Score = p => p.Instruction == "I2" ? 0.8 : 0.5 }, first, second, strategy: "p -> w"));

        better.InstructionOverrides.ShouldBe(new Dictionary<string, string> { ["x"] = "I2", ["y"] = "J1" });
        better.DemosBySubmodule["x"].ShouldBe([d1[1], d1[0]]);
        better.DemosBySubmodule["y"].ShouldBe([d1[1]]);
        better.ChatClients["x"].ShouldBeSameAs(tunedX);
        better.ChatClients.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_tie_goes_to_the_earliest_step()
    {
        var log = new List<string>();
        int calls = 0;
        var prompt = new Stub("p", log, p => Instruct(p, $"P{++calls}"));

        var result = await Run(Together(new World { Score = _ => 0.5 }, prompt, strategy: "p -> p -> p"));

        result.StepScores.ShouldBe([0.5, 0.5, 0.5]);
        result.InstructionOverrides["__single__"].ShouldBe("P1");
    }

    [Fact]
    public async Task Progress_reports_each_step_with_the_scores_so_far()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var reports = new List<OptimizerProgress>();

        await Run(Together(new World(), prompt, weight), new SyncProgress<OptimizerProgress>(reports.Add));

        reports.Select(r => r.Phase).ShouldBe(["prompt", "weight", "prompt"]);
        reports.Select(r => r.Step).ShouldBe([1, 2, 3]);
        reports.ShouldAllBe(r => r.TotalSteps == 3);
        reports.Select(r => r.BestScore).ShouldBe([0.4, 0.9, 0.9]);
        ((double[])reports[0].Extras["StepScores"]!).ShouldBe([0.4]);
        ((double[])reports[1].Extras["StepScores"]!).ShouldBe([0.4, 0.9]);
        ((double[])reports[2].Extras["StepScores"]!).ShouldBe([0.4, 0.9, 0.6]);
        reports.ShouldAllBe(r => (int)r.Extras["RunnerErrors"]! == 0);
    }

    [Fact]
    public async Task Every_step_is_scored_on_the_valset_and_the_steps_get_the_whole_trainset()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var world = new World();
        var valset = Samples("v", 3);
        var trainset = Samples("t", 5);

        await Run(Together(world, prompt, weight, valset: valset), trainset: trainset);

        world.Scored.Select(s => s.Question).ShouldBe(
            [.. Enumerable.Repeat(valset.Select(Q), 3).SelectMany(q => q)]);
        prompt.Trainsets.ShouldAllBe(t => ReferenceEquals(t, trainset));
        weight.Trainsets.Single().ShouldBeSameAs(trainset);
    }

    [Fact]
    public async Task Without_a_valset_a_seeded_holdout_of_the_trainset_scores_the_steps_and_is_kept_from_them()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var world = new World();
        var trainset = Samples("t", 10);

        await Run(NoValset(world, prompt, weight, 5), trainset: trainset);

        var held = world.Scored.Select(s => s.Question).Distinct().ToList();
        held.Count.ShouldBe(2);
        var trained = prompt.Trainsets[0].Select(Q).ToList();
        trained.Count.ShouldBe(8);
        trained.ShouldNotContain(held[0]);
        trained.ShouldNotContain(held[1]);
        trained.Concat(held).Order().ShouldBe(trainset.Select(Q).Order());
        // Every step sees the same training part.
        prompt.Trainsets.Concat(weight.Trainsets).ShouldAllBe(t => t.Select(Q).SequenceEqual(trained));
    }

    static BetterTogether<Prog> NoValset(World world, IOptimizer<Prog> prompt, IOptimizer<Prog> weight, int seed) =>
        new(world.RunAsync, prompt, weight, seed: seed);

    [Fact]
    public async Task The_holdout_follows_the_seed()
    {
        async Task<List<string>> Held(int seed)
        {
            var (prompt, weight, _) = PromptWeightPrompt();
            var world = new World();
            await Run(NoValset(world, prompt, weight, seed), trainset: Samples("t", 30));
            return [.. world.Scored.Select(s => s.Question).Distinct().Order()];
        }

        (await Held(1)).ShouldBe(await Held(1));
        (await Held(1)).ShouldNotBe(await Held(2));
    }

    [Fact]
    public async Task One_example_trains_and_validates()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var world = new World();
        var trainset = Samples("t", 1);

        await Run(NoValset(world, prompt, weight, 0), trainset: trainset);

        prompt.Trainsets[0].ShouldBeSameAs(trainset);
        world.Scored.Select(s => s.Question).Distinct().ShouldBe(["t0"]);
    }

    // ---- errors ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_run_scores_zero_and_is_counted()
    {
        var (prompt, weight, _) = PromptWeightPrompt();
        var world = new World { Fault = e => Q(e) == "v1" ? new InvalidOperationException("boom") : null };
        var reports = new List<OptimizerProgress>();
        var stopped = new ConcurrentBag<Activity>();
        using var source = new ActivitySource("BetterTogetherTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("run");

        var result = await Run(Together(world, prompt, weight), new SyncProgress<OptimizerProgress>(reports.Add));

        // Two validation examples; the one that fails scores 0, so each step is half of its score.
        result.StepScores.ShouldBe([0.2, 0.45, 0.3]);
        reports.Last().Extras["RunnerErrors"].ShouldBe(3);
        var mine = stopped.Single(a => a.Parent == parent && (string?)a.GetTagItem("optimizer.type") == "BetterTogether");
        mine.GetTagItem("optimizer.runner_errors").ShouldBe(3);
        mine.GetTagItem("optimizer.final_score").ShouldBe(0.45);
    }

    [Fact]
    public async Task A_metric_exception_and_a_NaN_both_score_zero()
    {
        var (prompt, _, _) = PromptWeightPrompt();
        var reports = new List<OptimizerProgress>();

        var thrown = await Run(Together(new World(), prompt, strategy: "p"),
            new SyncProgress<OptimizerProgress>(reports.Add), metric: new ThrowingMetric());
        var nan = await Run(Together(new World { Score = _ => double.NaN }, prompt, strategy: "p"));

        thrown.StepScores.ShouldBe([0.0]);
        reports.Last().Extras["RunnerErrors"].ShouldBe(2);
        nan.StepScores.ShouldBe([0.0]);
    }

    [Fact]
    public async Task A_failing_step_propagates()
    {
        var failing = new Stub("p", [], _ => throw new OptimizerException("step failed"));

        var ex = await Should.ThrowAsync<OptimizerException>(() => Run(Together(new World(), failing, strategy: "p")));

        ex.Message.ShouldBe("step failed");
    }

    [Fact]
    public async Task Cancellation_before_the_start_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Run(Together(new World(), new Stub("p", [], p => Result(p)), strategy: "p"), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_between_steps_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var log = new List<string>();
        var first = new Stub("p", log, p =>
        {
            cts.Cancel();
            return Result(p);
        });
        var second = new Stub("w", log, p => Result(p));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Run(Together(new World(), first, second, strategy: "p -> w"), ct: cts.Token));

        log.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_inside_the_scoring_run_propagates_and_is_not_counted()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reports = new List<OptimizerProgress>();
        var world = new World
        {
            Fault = _ =>
            {
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            },
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Run(Together(world, new Stub("p", [], p => Result(p)), strategy: "p"),
                new SyncProgress<OptimizerProgress>(reports.Add), ct: cts.Token));

        reports.ShouldBeEmpty();
    }

    // ---- the default optimizers ---------------------------------------------------------------------------

    sealed class Provider : IFineTuneProvider
    {
        public List<FineTuneRequest> Requests { get; } = [];
        public Dictionary<string, ScriptedModel> Clients { get; } = [];

        public Task<IChatClient> FineTuneAsync(FineTuneRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var client = new ScriptedModel(messages => Right(QuestionIn(messages)));
            Clients[request.Submodule] = client;
            return Task.FromResult<IChatClient>(client);
        }
    }

    static string QuestionIn(IEnumerable<ChatMessage> messages) =>
        Enumerable.Range(0, 4).Select(i => $"q{i}").Single(q => Kit.UserText(messages).Contains(q));

    static string Right(string question) => $"a{question[1]}";

    [Fact]
    public async Task The_default_optimizers_add_demos_then_a_tuned_client_and_the_tuned_one_wins()
    {
        // The base model is right under demos, except for q3. The tuned model is always right.
        var baseModel = new ScriptedModel(messages =>
        {
            var question = QuestionIn(messages);
            return messages.Count > 2 && question != "q3" ? Right(question) : "wrong";
        });
        var provider = new Provider();
        var trainset = Enumerable.Range(0, 4).Select(i => Kit.Qa($"q{i}", $"a{i}")).ToList();
        var reports = new List<OptimizerProgress>();

        var result = await new BetterTogether<Predict<KitSig>>(
                Kit.RunAsync, fineTuneProvider: provider, strategy: "p -> w", valset: trainset, seed: 1)
            .CompileAsync(Predict.Create<KitSig>(baseModel), trainset, new AnswerMatch(), Kit.Context(baseModel),
                new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        result.StepScores.ShouldBe([0.75, 1.0]);
        result.Score.ShouldBe(1.0);
        reports.Select(r => r.Phase).ShouldBe(["prompt", "weight"]);

        // Step 1: nothing passed the metric, so the labeled examples became the demos.
        result.DemosBySubmodule["__single__"].Count.ShouldBe(4);
        // Step 2: the teacher was the program with those demos; q0 to q2 passed, and the data is what it sent:
        // system, four demo pairs and the question, then the response.
        var request = provider.Requests.Single();
        request.Submodule.ShouldBe("Predict");
        request.Examples.Select(e => QuestionIn(e)).ShouldBe(["q0", "q1", "q2"]);
        request.Examples.ShouldAllBe(e => e.Count == 11 && e.Last().Role == ChatRole.Assistant);

        var prediction = await result.Bind(Kit.Context(new ScriptedModel(_ => "base")))
            .ForwardAsync(new KitSig { Question = "q3" }, ct: Ct);
        prediction.Value.Answer.ShouldBe("a3");
        result.ChatClients["Predict"].ShouldBeSameAs(provider.Clients["Predict"]);
    }

    // ---- p -> w -> p on a real Predict: the last step searches on top of the first two ----------------

    sealed class PredictStep(
        Func<Predict<KitSig>, AmpelumContext, Task<OptimizedProgram<Predict<KitSig>>>> make) : IOptimizer<Predict<KitSig>>
    {
        public Task<OptimizedProgram<Predict<KitSig>>> CompileAsync(
            Predict<KitSig> program, IReadOnlyList<Example> trainset, IMetric metric, AmpelumContext context,
            IProgress<OptimizerProgress>? progress = null, CancellationToken ct = default) =>
            make(program, context);
    }

    // Step 1 sets an instruction, step 2 a tuned client, step 3 binds a candidate that sets only a demo (or an
    // instruction of its own) the way a search does, and runs it.
    static async Task<(ScriptedModel Base, ScriptedModel Tuned)> RunThreeSteps(string? thirdInstruction)
    {
        var baseModel = new ScriptedModel(_ => "no");
        var tunedModel = new ScriptedModel(_ => "yes");
        var examples = new List<Example> { Kit.Qa("q0", "yes"), Kit.Qa("q1", "yes") };

        // One prompt optimizer serves both p steps: the first call sets the instruction, the second searches.
        int promptCalls = 0;
        var prompt = new PredictStep(async (p, context) =>
        {
            if (++promptCalls == 1)
                return new OptimizedProgram<Predict<KitSig>>(
                    p, new Dictionary<string, IReadOnlyList<Example>>(),
                    new Dictionary<string, string> { ["__single__"] = "STEP-1-INSTRUCTION" });

            var candidate = new OptimizedProgram<Predict<KitSig>>(
                p,
                new Dictionary<string, IReadOnlyList<Example>> { ["__single__"] = [Kit.Qa("d", "yes")] },
                thirdInstruction is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["__single__"] = thirdInstruction });
            await candidate.Bind(context).ForwardAsync(new KitSig { Question = "candidate" }, ct: Ct);
            return candidate;
        });
        var second = new PredictStep((p, _) => Task.FromResult(new OptimizedProgram<Predict<KitSig>>(
            p, new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>())
        {
            ChatClients = new Dictionary<string, IChatClient> { ["__single__"] = tunedModel },
        }));

        await new BetterTogether<Predict<KitSig>>(Kit.RunAsync, prompt, second, strategy: "p -> w -> p", valset: examples)
            .CompileAsync(Predict.Create<KitSig>(baseModel), examples, new AnswerMatch(), Kit.Context(baseModel), ct: Ct);
        return (baseModel, tunedModel);
    }

    [Fact]
    public async Task The_third_step_scores_its_candidates_with_the_tuned_client_and_the_first_steps_instruction()
    {
        var (baseModel, tunedModel) = await RunThreeSteps(thirdInstruction: null);

        // The candidate of step 3 is the call whose question is "candidate".
        baseModel.Calls.ShouldNotContain(c => Kit.UserText(c).Contains("candidate"));
        var call = tunedModel.Calls.Single(c => Kit.UserText(c).Contains("candidate"));
        Kit.SystemText(call).ShouldContain("STEP-1-INSTRUCTION");
    }

    [Fact]
    public async Task The_third_step_replaces_the_first_steps_instruction_when_it_sets_its_own()
    {
        var (_, tunedModel) = await RunThreeSteps(thirdInstruction: "STEP-3-INSTRUCTION");

        var call = tunedModel.Calls.Single(c => Kit.UserText(c).Contains("candidate"));
        Kit.SystemText(call).ShouldContain("STEP-3-INSTRUCTION");
        Kit.SystemText(call).ShouldNotContain("STEP-1-INSTRUCTION");
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_run()
    {
        async Task<OptimizedProgram<Predict<KitSig>>> Once()
        {
            var baseModel = new ScriptedModel(messages => messages.Count > 2 ? Right(QuestionIn(messages)) : "wrong");
            var trainset = Enumerable.Range(0, 4).Select(i => Kit.Qa($"q{i}", $"a{i}")).ToList();
            return await new BetterTogether<Predict<KitSig>>(
                    Kit.RunAsync, fineTuneProvider: new Provider(), valset: trainset, seed: 3)
                .CompileAsync(Predict.Create<KitSig>(baseModel), trainset, new AnswerMatch(), Kit.Context(baseModel), ct: Ct);
        }

        var a = await Once();
        var b = await Once();

        b.StepScores.ShouldBe(a.StepScores);
        b.Score.ShouldBe(a.Score);
        b.DemosBySubmodule["__single__"].Select(Kit.Question).ShouldBe(a.DemosBySubmodule["__single__"].Select(Kit.Question));
    }
}
