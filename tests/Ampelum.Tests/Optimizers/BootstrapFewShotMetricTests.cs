using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Tests.Optimizers;

/// <summary>A bootstrapped demo must pass the metric; a wrong answer never becomes a demo.</summary>
public sealed class BootstrapFewShotMetricTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Prog
    {
        public Prog WithContext(AmpelumContext context) => this;
    }

    sealed class Metric(Func<object, object, double> score) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => score(actual, expected);
    }

    static readonly IMetric Exact = new AnswerMatch();

    static IReadOnlyList<Example> Trainset() =>
        [Kit.Qa("q0", "a0"), Kit.Qa("q1", "a1"), Kit.Qa("q2", "a2"), Kit.Qa("q3", "a3")];

    static TraceRecord Trace(string module, Example example, string answer, Exception? error = null) =>
        new(Guid.NewGuid(), module, typeof(KitSig), example.Inputs,
            new Dictionary<string, object?> { ["Answer"] = answer },
            [], answer, null, TimeSpan.Zero, error, new Dictionary<string, object?>());

    // The program answers the examples whose question is in the set right and every other one wrong.
    static BootstrapFewShot<Prog>.ProgramRunner Runner(params string[] right) =>
        (_, example, ctx, _) =>
        {
            ctx.TraceSink.Write(Trace("qa", example, right.Contains(Kit.Question(example)) ? Kit.Answer(example) : "wrong"));
            return Task.CompletedTask;
        };

    static IEnumerable<string> Answers(IEnumerable<Example> demos) => demos.Select(Kit.Answer);

    [Fact]
    public void Threshold_defaults_to_one_and_is_settable()
    {
        new BootstrapFewShot<Prog>(Runner()).MetricThreshold.ShouldBe(1.0);
        new BootstrapFewShot<Prog>(Runner(), metricThreshold: 0.25).MetricThreshold.ShouldBe(0.25);
    }

    [Fact]
    public async Task A_trace_with_a_wrong_answer_is_not_a_demo()
    {
        var optimizer = new BootstrapFewShot<Prog>(Runner("q0", "q2"));

        var result = await optimizer.CompileAsync(
            new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.ShouldBe(["qa"]);
        Answers(result.DemosBySubmodule["qa"]).ShouldBe(["a0", "a2"]);
    }

    [Fact]
    public async Task The_demo_cap_counts_only_traces_that_passed()
    {
        // The first two examples fail; the cap of two is filled by the passing ones after them.
        var optimizer = new BootstrapFewShot<Prog>(Runner("q2", "q3"), maxBootstrappedDemos: 2);

        var result = await optimizer.CompileAsync(
            new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        Answers(result.DemosBySubmodule["qa"]).ShouldBe(["a2", "a3"]);
    }

    [Fact]
    public async Task When_no_trace_passes_the_demos_are_the_labeled_examples_not_the_wrong_answers()
    {
        var optimizer = new BootstrapFewShot<Prog>(Runner());

        var result = await optimizer.CompileAsync(
            new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.ShouldBe(["__single__"]);
        Answers(result.DemosBySubmodule["__single__"]).ShouldBe(["a0", "a1", "a2", "a3"]);
    }

    [Fact]
    public async Task A_lower_threshold_keeps_traces_that_score_at_or_above_it()
    {
        // 1 for a right answer, 0.5 for a wrong one.
        var partial = new Metric((actual, expected) =>
            Equals(((IReadOnlyDictionary<string, object?>)actual)["Answer"],
                   ((IReadOnlyDictionary<string, object?>)expected)["Answer"]) ? 1.0 : 0.5);

        var strict = await new BootstrapFewShot<Prog>(Runner("q0"))
            .CompileAsync(new Prog(), Trainset(), partial, TestHelpers.MakeContext(), ct: Ct);
        var lenient = await new BootstrapFewShot<Prog>(Runner("q0"), metricThreshold: 0.5)
            .CompileAsync(new Prog(), Trainset(), partial, TestHelpers.MakeContext(), ct: Ct);

        Answers(strict.DemosBySubmodule["qa"]).ShouldBe(["a0"]);
        Answers(lenient.DemosBySubmodule["qa"]).ShouldBe(["a0", "wrong", "wrong", "wrong"]);
    }

    [Fact]
    public async Task A_metric_that_throws_on_a_trace_counts_as_a_fail()
    {
        var throwing = new Metric((_, _) => throw new InvalidOperationException("metric failed"));

        var result = await new BootstrapFewShot<Prog>(Runner("q0", "q1", "q2", "q3"))
            .CompileAsync(new Prog(), Trainset(), throwing, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.ShouldBe(["__single__"]);
    }

    [Fact]
    public async Task A_metric_that_cancels_propagates()
    {
        var cancelling = new Metric((_, _) => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new BootstrapFewShot<Prog>(Runner("q0"))
                .CompileAsync(new Prog(), Trainset(), cancelling, TestHelpers.MakeContext(), ct: Ct));
    }

    [Fact]
    public async Task Random_search_inherits_the_filter()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<Prog>(Runner("q0", "q2"), numCandidates: 3, seed: 5);

        var result = await optimizer.CompileAsync(
            new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        var answers = Answers(result.DemosBySubmodule["qa"]).ToList();
        answers.ShouldNotBeEmpty();
        answers.ShouldAllBe(a => a == "a0" || a == "a2");
    }

    // A metric on a 0 to 100 scale: 80 for a right answer, 20 for a wrong one. The default threshold of 1 keeps both.
    static readonly IMetric Percent = new Metric((actual, expected) =>
        Equals(((IReadOnlyDictionary<string, object?>)actual)["Answer"],
               ((IReadOnlyDictionary<string, object?>)expected)["Answer"]) ? 80.0 : 20.0);

    [Fact]
    public async Task Random_search_takes_a_threshold_for_a_metric_on_another_scale()
    {
        var byDefault = await new BootstrapFewShotWithRandomSearch<Prog>(Runner("q0", "q2"), numCandidates: 1, seed: 5)
            .CompileAsync(new Prog(), Trainset(), Percent, TestHelpers.MakeContext(), ct: Ct);
        var tuned = new BootstrapFewShotWithRandomSearch<Prog>(
            Runner("q0", "q2"), numCandidates: 1, seed: 5, metricThreshold: 50);
        var result = await tuned.CompileAsync(new Prog(), Trainset(), Percent, TestHelpers.MakeContext(), ct: Ct);

        new BootstrapFewShotWithRandomSearch<Prog>(Runner()).MetricThreshold.ShouldBe(1.0);
        tuned.MetricThreshold.ShouldBe(50);
        Answers(byDefault.DemosBySubmodule["qa"]).ShouldContain("wrong");
        Answers(result.DemosBySubmodule["qa"]).OrderBy(a => a).ShouldBe(["a0", "a2"]);
    }

    [Fact]
    public async Task MiproV2_takes_a_threshold_for_a_metric_on_another_scale()
    {
        async Task<List<string>> Proposals(double? threshold)
        {
            var proposer = new TextModel("a summary", "an instruction");
            var optimizer = threshold is null
                ? new MiproV2<Prog>(MiproRunner, proposer, candidatesPerSubmodule: 1, numTrials: 2, numDemoSets: 2,
                    minibatchSize: 4, fullEvalEvery: 1, seed: 3)
                : new MiproV2<Prog>(MiproRunner, proposer, candidatesPerSubmodule: 1, numTrials: 2, numDemoSets: 2,
                    minibatchSize: 4, fullEvalEvery: 1, seed: 3, metricThreshold: threshold.Value);
            await optimizer.CompileAsync(new Prog(), Trainset(), Percent, TestHelpers.MakeContext(), ct: Ct);
            return proposer.Calls.Skip(1).Select(c => Kit.UserText(c)).ToList();
        }

        new MiproV2<Prog>(MiproRunner, new TextModel("x")).MetricThreshold.ShouldBe(1.0);
        (await Proposals(null)).ShouldContain(p => p.Contains("Answer=wrong"));
        var tuned = await Proposals(50);
        tuned.ShouldNotBeEmpty();
        tuned.ShouldAllBe(p => p.Contains("Answer=a0") && !p.Contains("Answer=wrong"));
    }

    static Task<Example> MiproRunner(Prog program, Example example, AmpelumContext ctx, CancellationToken ct)
    {
        var right = Kit.Question(example) is "q0" or "q2";
        ctx.TraceSink.Write(Trace("qa", example, right ? Kit.Answer(example) : "wrong"));
        return Task.FromResult(example);
    }

    // ---- composed programs: the run is scored once ---------------------------------------------------------

    static TraceRecord Step(string module, Example example, string field, string value, Exception? error = null) =>
        new(Guid.NewGuid(), module, typeof(KitSig), example.Inputs,
            new Dictionary<string, object?> { [field] = value },
            [], value, null, TimeSpan.Zero, error, new Dictionary<string, object?>());

    // Two submodules: "draft" produces a field the example never has, "final" produces the answer.
    static BootstrapFewShot<Prog>.ProgramRunner TwoStageRunner(params string[] right) =>
        (_, example, ctx, _) =>
        {
            ctx.TraceSink.Write(Step("draft", example, "Draft", "notes"));
            ctx.TraceSink.Write(Step("final", example, "Answer",
                right.Contains(Kit.Question(example)) ? Kit.Answer(example) : "wrong"));
            return Task.CompletedTask;
        };

    [Fact]
    public async Task A_passing_run_keeps_the_traces_of_every_submodule()
    {
        var result = await new BootstrapFewShot<Prog>(TwoStageRunner("q0", "q2"))
            .CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.OrderBy(k => k).ShouldBe(["draft", "final"]);
        result.DemosBySubmodule["draft"].Count.ShouldBe(2);
        result.DemosBySubmodule["draft"].ShouldAllBe(d => d.Outputs.ContainsKey("Draft") && !d.Outputs.ContainsKey("Answer"));
        Answers(result.DemosBySubmodule["final"]).ShouldBe(["a0", "a2"]);
    }

    [Fact]
    public async Task A_failing_run_keeps_no_trace_of_any_submodule()
    {
        var result = await new BootstrapFewShot<Prog>(TwoStageRunner())
            .CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.ShouldBe(["__single__"]);
    }

    [Fact]
    public async Task A_run_whose_last_trace_has_an_error_keeps_nothing_and_a_trace_with_an_error_is_skipped()
    {
        var failedLast = await new BootstrapFewShot<Prog>((_, example, ctx, _) =>
            {
                ctx.TraceSink.Write(Step("draft", example, "Draft", "notes"));
                ctx.TraceSink.Write(Step("final", example, "Answer", Kit.Answer(example), new InvalidOperationException("x")));
                return Task.CompletedTask;
            })
            .CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);
        var failedFirst = await new BootstrapFewShot<Prog>((_, example, ctx, _) =>
            {
                ctx.TraceSink.Write(Step("draft", example, "Draft", "notes", new InvalidOperationException("x")));
                ctx.TraceSink.Write(Step("final", example, "Answer", Kit.Answer(example)));
                return Task.CompletedTask;
            })
            .CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        failedLast.DemosBySubmodule.Keys.ShouldBe(["__single__"]);
        failedFirst.DemosBySubmodule.Keys.ShouldBe(["final"]);
    }

    [Fact]
    public async Task A_run_whose_runner_threw_is_not_scored_even_when_its_partial_traces_would_pass()
    {
        var lenient = new Metric((_, _) => 1.0);

        // The runner writes the intermediate trace, then throws; only q1 and q3 run to the end.
        var result = await new BootstrapFewShot<Prog>((_, example, ctx, _) =>
            {
                ctx.TraceSink.Write(Step("draft", example, "Draft", "notes"));
                if (Kit.Question(example) is "q0" or "q2") throw new InvalidOperationException("runner failed");
                ctx.TraceSink.Write(Step("final", example, "Answer", Kit.Answer(example)));
                return Task.CompletedTask;
            })
            .CompileAsync(new Prog(), Trainset(), lenient, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule["draft"].Count.ShouldBe(2);
        Answers(result.DemosBySubmodule["final"]).ShouldBe(["a1", "a3"]);
    }

    [Fact]
    public async Task A_run_with_no_trace_keeps_nothing()
    {
        var result = await new BootstrapFewShot<Prog>((_, _, _, _) => Task.CompletedTask)
            .CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        result.DemosBySubmodule.Keys.ShouldBe(["__single__"]);
    }

    [Fact]
    public async Task MiproV2_inherits_the_filter()
    {
        var proposer = new TextModel("a summary", "an instruction");
        var optimizer = new MiproV2<Prog>(
            (_, example, ctx, _) =>
            {
                var right = Kit.Question(example) is "q0" or "q2";
                ctx.TraceSink.Write(Trace("qa", example, right ? Kit.Answer(example) : "wrong"));
                return Task.FromResult(example);
            },
            proposer,
            candidatesPerSubmodule: 1, numTrials: 2, numDemoSets: 2, minibatchSize: 4, fullEvalEvery: 1, seed: 3);

        await optimizer.CompileAsync(new Prog(), Trainset(), Exact, TestHelpers.MakeContext(), ct: Ct);

        var proposals = proposer.Calls.Skip(1).Select(c => Kit.UserText(c)).ToList();
        proposals.ShouldNotBeEmpty();
        proposals.ShouldAllBe(p => p.Contains("Answer=a0") && !p.Contains("Answer=wrong"));
    }
}
