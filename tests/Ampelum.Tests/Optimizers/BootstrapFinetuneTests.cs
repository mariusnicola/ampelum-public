using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class BootstrapFinetuneTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Signature = "Given a question, produce an answer.";

    static List<Example> Trainset() =>
        [Kit.Qa("q0", "a0"), Kit.Qa("q1", "a1"), Kit.Qa("q2", "a2"), Kit.Qa("q3", "a3")];

    static string QuestionIn(IEnumerable<ChatMessage> messages) =>
        Enumerable.Range(0, 4).Select(i => $"q{i}").Single(q => Kit.UserText(messages).Contains(q));

    static string Right(string question) => $"a{question[1]}";

    // Answers right for the listed questions and wrong for the others.
    static ScriptedModel Teacher(params string[] right) =>
        new(messages => right.Contains(QuestionIn(messages)) ? Right(QuestionIn(messages)) : "wrong");

    sealed class Provider : IFineTuneProvider
    {
        readonly object _gate = new();
        public List<FineTuneRequest> Requests { get; } = [];
        public Dictionary<string, ScriptedModel> Clients { get; } = [];
        public Exception? Fault { get; init; }
        public Action? OnCall { get; init; }

        public Task<IChatClient> FineTuneAsync(FineTuneRequest request, CancellationToken ct = default)
        {
            OnCall?.Invoke();
            lock (_gate) Requests.Add(request);
            if (Fault is not null) throw Fault;
            var client = new ScriptedModel(_ => $"tuned-{request.Submodule}");
            lock (_gate) Clients[request.Submodule] = client;
            return Task.FromResult<IChatClient>(client);
        }
    }

    sealed class Pair(Predict<KitSig> a, Predict<KitSig> b) : IComposedModule
    {
        public Predict<KitSig> A => a;
        public Predict<KitSig> B => b;

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["a"] = a, ["b"] = b };

        public object Rebind(IReadOnlyDictionary<string, object> s) =>
            new Pair((Predict<KitSig>)s["a"], (Predict<KitSig>)s["b"]);
    }

    sealed class Metric(Func<object, object, double> score) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => score(actual, expected);
    }

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    {
        public void Report(T value) => cb(value);
    }

    static BootstrapFinetune<Predict<KitSig>> Optimizer(Provider provider, Predict<KitSig>? teacher = null, double threshold = 1.0) =>
        new(teacher is null ? Rebound : Kit.Trace, provider, teacher, threshold);

    // The program is unbound; the runner binds it to the context the optimizer hands over, so the
    // prediction writes its trace to the sink of that context itself.
    static Task Rebound(Predict<KitSig> program, Example example, AmpelumContext context, CancellationToken ct) =>
        program.WithContext(context).ForwardAsync(new KitSig { Question = Kit.Question(example) }, ct: ct);

    static Task<OptimizedProgram<Predict<KitSig>>> Compile(
        BootstrapFinetune<Predict<KitSig>> optimizer, IChatClient model, IProgress<OptimizerProgress>? progress = null) =>
        optimizer.CompileAsync(
            Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(model), progress, Ct);

    // ---- constructor and arguments --------------------------------------------------------------

    [Fact]
    public void Ctor_NullRunner_Throws() =>
        Should.Throw<ArgumentNullException>(() => new BootstrapFinetune<Predict<KitSig>>(null!, new Provider()));

    [Fact]
    public void Ctor_NullProvider_Throws() =>
        Should.Throw<ArgumentNullException>(() => new BootstrapFinetune<Predict<KitSig>>(Kit.Trace, null!));

    [Fact]
    public void The_threshold_defaults_to_one_and_is_settable()
    {
        Optimizer(new Provider()).MetricThreshold.ShouldBe(1.0);
        Optimizer(new Provider(), threshold: 0.4).MetricThreshold.ShouldBe(0.4);
    }

    [Fact]
    public async Task CompileAsync_NullArguments_Throw()
    {
        var sut = Optimizer(new Provider());
        var ctx = Kit.Context(Teacher());
        var program = Predict<KitSig>.FromAmbient();
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(null!, Trainset(), new AnswerMatch(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(program, null!, new AnswerMatch(), ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(program, Trainset(), null!, ctx, ct: Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => sut.CompileAsync(program, Trainset(), new AnswerMatch(), null!, ct: Ct));
    }

    // ---- metric filtering and chat-format data ----------------------------------------------------

    [Fact]
    public async Task Only_traces_that_pass_the_metric_become_training_examples()
    {
        var provider = new Provider();

        await Compile(Optimizer(provider), Teacher("q0", "q2"));

        var request = provider.Requests.Single();
        request.Submodule.ShouldBe("Predict");
        request.Examples.Count.ShouldBe(2);
        request.Examples.Select(e => QuestionIn(e)).ShouldBe(["q0", "q2"]);
    }

    [Fact]
    public async Task A_training_example_is_system_user_assistant_built_by_the_adapter_from_the_trace()
    {
        var provider = new Provider();
        var model = Teacher("q1");

        await Compile(Optimizer(provider), model);

        var example = provider.Requests.Single().Examples.Single();
        example.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User, ChatRole.Assistant]);
        Kit.SystemText(example).ShouldContain(Signature);
        // What the adapter formats for the trace's inputs is what the model was sent.
        var sent = model.Calls.Single(c => QuestionIn(c) == "q1");
        example[0].Text.ShouldBe(sent[0].Text);
        example[1].Text.ShouldBe(sent[1].Text);
        example[1].Text.ShouldContain("q1");
        // The assistant message is the response that passed the metric.
        example[2].Text.ShouldContain("a1");
    }

    [Fact]
    public async Task A_training_example_is_the_messages_the_submodule_really_sent_plus_its_response()
    {
        var provider = new Provider();
        var model = Teacher("q0");
        // The teacher is bound with a demo and an instruction override; the context's adapter never sees either.
        var teacher = new OptimizedProgram<Predict<KitSig>>(
            Predict<KitSig>.FromAmbient(),
            new Dictionary<string, IReadOnlyList<Example>> { ["Predict"] = [Kit.Qa("q3", "a3")] },
            new Dictionary<string, string> { ["Predict"] = "OVERRIDE-INSTRUCTION" }).Bind(Kit.Context(model));

        await Optimizer(provider, teacher).CompileAsync(
            Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher()), ct: Ct);

        var example = provider.Requests.Single().Examples.Single();
        var sent = model.Calls.Single(c => QuestionIn(c) == "q0");
        sent.Count.ShouldBeGreaterThan(3);
        Kit.SystemText(sent).ShouldContain("OVERRIDE-INSTRUCTION");
        sent.Any(m => m.Text.Contains("a3")).ShouldBeTrue();
        example.Count.ShouldBe(sent.Count + 1);
        example.Take(sent.Count).Select(m => (m.Role, m.Text)).ShouldBe(sent.Select(m => (m.Role, m.Text)));
        example[^1].Role.ShouldBe(ChatRole.Assistant);
        example[^1].Text.ShouldContain("a0");
    }

    [Fact]
    public async Task A_lower_threshold_keeps_traces_that_score_at_or_above_it()
    {
        var provider = new Provider();
        var half = new Metric((actual, expected) =>
            Equals(((IReadOnlyDictionary<string, object?>)actual)["Answer"],
                   ((IReadOnlyDictionary<string, object?>)expected)["Answer"]) ? 1.0 : 0.5);

        await new BootstrapFinetune<Predict<KitSig>>(Rebound, provider, metricThreshold: 0.5)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), half, Kit.Context(Teacher("q0")), ct: Ct);

        provider.Requests.Single().Examples.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_NaN_score_does_not_pass()
    {
        var provider = new Provider();

        await new BootstrapFinetune<Predict<KitSig>>(Rebound, provider, metricThreshold: 0.0)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), new Metric((_, _) => double.NaN),
                Kit.Context(Teacher("q0")), ct: Ct);

        provider.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trace_with_an_error_is_never_kept()
    {
        var provider = new Provider();
        Task Runner(Predict<KitSig> program, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            ctx.TraceSink.Write(new TraceRecord(
                Guid.NewGuid(), "Predict", typeof(KitSig), example.Inputs, example.Outputs, [], "x", null,
                TimeSpan.Zero, new InvalidOperationException("failed"), new Dictionary<string, object?>()));
            return Task.CompletedTask;
        }

        var result = await new BootstrapFinetune<Predict<KitSig>>(Runner, provider)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher()), ct: Ct);

        provider.Requests.ShouldBeEmpty();
        result.ChatClients.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_passing_run_trains_every_submodule_even_when_its_outputs_have_other_field_names()
    {
        var provider = new Provider();
        static TraceRecord Step(string module, Example example, string field, string value) =>
            new(Guid.NewGuid(), module, typeof(KitSig), example.Inputs,
                new Dictionary<string, object?> { [field] = value },
                [new ChatMessage(ChatRole.User, Kit.Question(example))], value, null, TimeSpan.Zero, null,
                new Dictionary<string, object?>());

        // "draft" produces a field the expected outputs never have; "final" (the last call) produces the answer.
        Task Runner(Predict<KitSig> program, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            ctx.TraceSink.Write(Step("draft", example, "Draft", "notes"));
            ctx.TraceSink.Write(Step("final", example, "Answer", Kit.Question(example) is "q0" or "q2" ? Kit.Answer(example) : "wrong"));
            return Task.CompletedTask;
        }

        var result = await new BootstrapFinetune<Predict<KitSig>>(Runner, provider)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher()), ct: Ct);

        provider.Requests.Select(r => r.Submodule).ShouldBe(["draft", "final"]);
        provider.Requests.ShouldAllBe(r => r.Examples.Count == 2);
        result.ChatClients.Keys.ShouldBe(["draft", "final"]);
    }

    [Fact]
    public async Task Nothing_kept_means_no_provider_call_and_the_program_comes_back_unchanged()
    {
        var provider = new Provider();
        var program = Predict<KitSig>.FromAmbient();

        var result = await Optimizer(provider).CompileAsync(
            program, Trainset(), new AnswerMatch(), Kit.Context(Teacher()), ct: Ct);

        provider.Requests.ShouldBeEmpty();
        result.Program.ShouldBeSameAs(program);
        result.ChatClients.ShouldBeEmpty();
        result.DemosBySubmodule.ShouldBeEmpty();
        result.InstructionOverrides.ShouldBeEmpty();
        result.Score.ShouldBeNull();
        result.TrainsetSize.ShouldBe(4);
    }

    // ---- teacher ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_teacher_runs_the_trainset_and_the_result_is_the_student_program()
    {
        var provider = new Provider();
        var teacherModel = Teacher("q0", "q1", "q2", "q3");
        var contextModel = Teacher();
        var teacher = Predict.Create<KitSig>(teacherModel);
        var student = Predict<KitSig>.FromAmbient();

        var result = await Optimizer(provider, teacher).CompileAsync(
            student, Trainset(), new AnswerMatch(), Kit.Context(contextModel), ct: Ct);

        teacherModel.Calls.Count.ShouldBe(4);
        contextModel.Calls.ShouldBeEmpty();
        provider.Requests.Single().Examples.Count.ShouldBe(4);
        result.Program.ShouldBeSameAs(student);
    }

    // ---- grouping and binding -------------------------------------------------------------------------

    static Pair BoundPair(IChatClient model) =>
        new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string> { ["a"] = "A-mode", ["b"] = "B-mode" }).Bind(Kit.Context(model));

    // Module a answers right for every question, module b (the last call of a run) only for q0 and q1.
    static ScriptedModel PairTeacher() =>
        new(messages =>
        {
            var question = QuestionIn(messages);
            return Kit.SystemText(messages).Contains("A-mode") || question is "q0" or "q1" ? Right(question) : "wrong";
        });

    static async Task RunPair(Pair pair, Example example, AmpelumContext ctx, CancellationToken ct)
    {
        var a = await pair.A.ForwardAsync(new KitSig { Question = Kit.Question(example) }, ct: ct);
        ctx.TraceSink.Write(a.Trace);
        var b = await pair.B.ForwardAsync(new KitSig { Question = Kit.Question(example) }, ct: ct);
        ctx.TraceSink.Write(b.Trace);
    }

    [Fact]
    public async Task Kept_traces_are_grouped_per_submodule_and_the_provider_is_called_once_for_each()
    {
        var provider = new Provider();
        var model = PairTeacher();
        var pair = BoundPair(model);

        var result = await new BootstrapFinetune<Pair>(RunPair, provider)
            .CompileAsync(pair, Trainset(), new AnswerMatch(), Kit.Context(model), ct: Ct);

        provider.Requests.Select(r => r.Submodule).ShouldBe(["a", "b"]);
        // A run is scored once, on its last call (module b): only the runs of q0 and q1 pass, and both
        // modules keep the trace of those runs.
        provider.Requests[0].Examples.Select(e => QuestionIn(e)).ShouldBe(["q0", "q1"]);
        provider.Requests[1].Examples.Select(e => QuestionIn(e)).ShouldBe(["q0", "q1"]);
        result.ChatClients.Keys.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_submodule_without_a_kept_trace_gets_no_provider_call_and_no_client()
    {
        var provider = new Provider();
        var model = new ScriptedModel(messages => Right(QuestionIn(messages)));
        var pair = BoundPair(model);

        // The runs only ever call module a.
        var result = await new BootstrapFinetune<Pair>(async (p, example, ctx, ct) =>
            {
                var a = await p.A.ForwardAsync(new KitSig { Question = Kit.Question(example) }, ct: ct);
                ctx.TraceSink.Write(a.Trace);
            }, provider)
            .CompileAsync(pair, Trainset(), new AnswerMatch(), Kit.Context(model), ct: Ct);

        provider.Requests.Select(r => r.Submodule).ShouldBe(["a"]);
        result.ChatClients.Keys.ShouldBe(["a"]);

        // The submodule without a tuned client takes the client of the context.
        var baseModel = new ScriptedModel(_ => "base");
        var bound = result.Bind(Kit.Context(baseModel));
        var a = await bound.A.ForwardAsync(new KitSig { Question = "q0" }, ct: Ct);
        var b = await bound.B.ForwardAsync(new KitSig { Question = "q0" }, ct: Ct);
        a.Value.Answer.ShouldBe("tuned-a");
        b.Value.Answer.ShouldBe("base");
    }

    [Fact]
    public async Task Each_tuned_client_is_bound_to_its_own_submodule()
    {
        var provider = new Provider();
        var model = PairTeacher();

        var result = await new BootstrapFinetune<Pair>(RunPair, provider)
            .CompileAsync(BoundPair(model), Trainset(), new AnswerMatch(), Kit.Context(model), ct: Ct);

        var baseModel = new ScriptedModel(_ => "base");
        var bound = result.Bind(Kit.Context(baseModel));
        var a = await bound.A.ForwardAsync(new KitSig { Question = "q0" }, ct: Ct);
        var b = await bound.B.ForwardAsync(new KitSig { Question = "q0" }, ct: Ct);

        result.ChatClients["a"].ShouldBeSameAs(provider.Clients["a"]);
        result.ChatClients["b"].ShouldBeSameAs(provider.Clients["b"]);
        a.Value.Answer.ShouldBe("tuned-a");
        b.Value.Answer.ShouldBe("tuned-b");
        provider.Clients["a"].Calls.Count.ShouldBe(1);
        provider.Clients["b"].Calls.Count.ShouldBe(1);
        baseModel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_single_module_program_gets_the_tuned_client()
    {
        var provider = new Provider();

        var result = await Compile(Optimizer(provider), Teacher("q0", "q2"));

        var baseModel = new ScriptedModel(_ => "base");
        var prediction = await result.Bind(Kit.Context(baseModel))
            .ForwardAsync(new KitSig { Question = "q0" }, ct: Ct);

        prediction.Value.Answer.ShouldBe("tuned-Predict");
        baseModel.Calls.ShouldBeEmpty();
    }

    // ---- errors, progress, cancellation --------------------------------------------------------------

    [Fact]
    public async Task A_runner_that_throws_scores_zero_is_counted_and_keeps_nothing_from_that_run()
    {
        var provider = new Provider();
        var reports = new List<OptimizerProgress>();
        var stopped = new ConcurrentBag<Activity>();
        using var source = new ActivitySource("BootstrapFinetuneTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = source.StartActivity("run");
        // q1 writes a passing trace and then fails; a timeout (a cancellation that is not ours) counts too.
        async Task Runner(Predict<KitSig> program, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            await Rebound(program, example, ctx, ct);
            if (Kit.Question(example) == "q1") throw new InvalidOperationException("boom");
            if (Kit.Question(example) == "q3") throw new TaskCanceledException("timeout");
        }

        await new BootstrapFinetune<Predict<KitSig>>(Runner, provider)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(),
                Kit.Context(Teacher("q0", "q1", "q2", "q3")), new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        provider.Requests.Single().Examples.Select(e => QuestionIn(e)).ShouldBe(["q0", "q2"]);
        reports.Last().Extras["RunnerErrors"].ShouldBe(2);
        var mine = stopped.Single(a => a.Parent == parent && (string?)a.GetTagItem("optimizer.type") == "BootstrapFinetune");
        mine.GetTagItem("optimizer.runner_errors").ShouldBe(2);
        mine.GetTagItem("optimizer.kept_traces").ShouldBe(2);
    }

    [Fact]
    public async Task A_metric_that_throws_is_counted_and_the_trace_is_not_kept()
    {
        var provider = new Provider();
        var reports = new List<OptimizerProgress>();

        await new BootstrapFinetune<Predict<KitSig>>(Rebound, provider)
            .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(),
                new Metric((_, _) => throw new InvalidOperationException("metric failed")),
                Kit.Context(Teacher("q0")), new SyncProgress<OptimizerProgress>(reports.Add), Ct);

        provider.Requests.ShouldBeEmpty();
        reports.Last().Extras["RunnerErrors"].ShouldBe(4);
    }

    [Fact]
    public async Task Progress_reports_each_example_then_each_provider_call()
    {
        var provider = new Provider();
        var reports = new List<OptimizerProgress>();

        await Compile(Optimizer(provider), Teacher("q0", "q2"), new SyncProgress<OptimizerProgress>(reports.Add));

        reports.Select(r => (r.Phase, r.Step, r.TotalSteps)).ShouldBe(
            [("teacher", 1, 4), ("teacher", 2, 4), ("teacher", 3, 4), ("teacher", 4, 4), ("finetune", 1, 1)]);
        reports.Select(r => r.Extras["KeptTraces"]).ShouldBe([1, 1, 2, 2, 2]);
        reports.Last().Extras["RunnerErrors"].ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_provider_throws_OptimizerException_naming_the_submodule()
    {
        var provider = new Provider { Fault = new InvalidOperationException("quota") };

        var ex = await Should.ThrowAsync<OptimizerException>(() => Compile(Optimizer(provider), Teacher("q0")));

        ex.Message.ShouldContain("provider failed for submodule 'Predict'");
        ex.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("quota");
    }

    [Fact]
    public async Task Cancellation_before_the_start_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(new Provider()).CompileAsync(
                Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher()), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_inside_the_runner_propagates_and_is_not_counted()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reports = new List<OptimizerProgress>();
        Task Runner(Predict<KitSig> program, Example example, AmpelumContext ctx, CancellationToken ct)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new BootstrapFinetune<Predict<KitSig>>(Runner, new Provider())
                .CompileAsync(Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher()),
                    new SyncProgress<OptimizerProgress>(reports.Add), cts.Token));

        reports.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_inside_the_provider_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var provider = new Provider
        {
            OnCall = () => cts.Cancel(),
            Fault = new OperationCanceledException(cts.Token),
        };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Optimizer(provider).CompileAsync(
                Predict<KitSig>.FromAmbient(), Trainset(), new AnswerMatch(), Kit.Context(Teacher("q0")), ct: cts.Token));
    }

    [Fact]
    public async Task Cancellation_between_provider_calls_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var provider = new Provider { OnCall = () => cts.Cancel() };
        var model = PairTeacher();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new BootstrapFinetune<Pair>(RunPair, provider)
                .CompileAsync(BoundPair(model), Trainset(), new AnswerMatch(), Kit.Context(model), ct: cts.Token));

        provider.Requests.Select(r => r.Submodule).ShouldBe(["a"]);
    }

    [Fact]
    public async Task The_same_inputs_give_the_same_training_data()
    {
        async Task<List<string>> Run()
        {
            var provider = new Provider();
            await Compile(Optimizer(provider), Teacher("q0", "q2"));
            return [.. provider.Requests.Single().Examples.Select(e => string.Join("|", e.Select(m => m.Role + ":" + m.Text)))];
        }

        (await Run()).ShouldBe(await Run());
    }
}
