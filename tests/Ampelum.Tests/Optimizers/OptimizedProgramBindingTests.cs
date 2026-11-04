using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Tests.Optimizers;

/// <summary>
/// How <see cref="OptimizedProgram{TProgram}.Bind"/> hands overrides and chat clients to a program: a bare
/// module must see its instruction override, and a submodule with a tuned client must call that client.
/// </summary>
public sealed class OptimizedProgramBindingTests
{
    const string Tuned = "TUNED: answer yes.";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly IReadOnlyDictionary<string, IReadOnlyList<Example>> NoDemos =
        new Dictionary<string, IReadOnlyList<Example>>();

    static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    // Answers yes under the tuned instruction and no otherwise.
    static ScriptedModel YesWhenTuned() =>
        new(messages => Kit.SystemText(messages).Contains(Tuned, StringComparison.Ordinal) ? "yes" : "no");

    static List<Example> Examples(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Kit.Qa($"q{i}", "yes"))];

    sealed class Pair(Predict<KitSig> a, Predict<KitSig> b) : IComposedModule
    {
        public Predict<KitSig> A => a;
        public Predict<KitSig> B => b;

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["a"] = a, ["b"] = b };

        public object Rebind(IReadOnlyDictionary<string, object> s) =>
            new Pair((Predict<KitSig>)s["a"], (Predict<KitSig>)s["b"]);
    }

    // ---- BUG-144: a bare Predict reads its override ----------------------------------------

    [Fact]
    public async Task A_bare_predict_sends_the_override_stored_under_the_single_name()
    {
        var model = new ScriptedModel(_ => "yes");
        var program = new OptimizedProgram<Predict<KitSig>>(
            Predict<KitSig>.FromAmbient(), NoDemos, new Dictionary<string, string> { ["__single__"] = Tuned });

        var bound = program.Bind(Kit.Context(model));
        await bound.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        Kit.SystemText(model.Calls.Single()).ShouldContain(Tuned);
    }

    [Fact]
    public async Task A_bare_predict_without_overrides_keeps_its_default_module_name()
    {
        var model = new ScriptedModel(_ => "yes");
        var program = new OptimizedProgram<Predict<KitSig>>(Predict<KitSig>.FromAmbient(), NoDemos, NoOverrides);

        var prediction = await program.Bind(Kit.Context(model))
            .ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        prediction.Trace.ModuleName.ShouldBe("Predict");
    }

    [Fact]
    public async Task GEPA_tuning_a_bare_predict_changes_its_system_prompt()
    {
        var model = YesWhenTuned();
        var examples = Examples(4);
        var optimizer = new GEPAOptimizer<Predict<KitSig>>(
            Kit.RunAsync, new TextModel(Tuned),
            new GepaOptions { MaxIterations = 2, MinibatchSize = 2, UseMerge = false },
            valset: examples);

        var result = await optimizer.CompileAsync(
            Predict<KitSig>.FromAmbient(), examples, new AnswerMatch(), Kit.Context(model), ct: Ct);

        result.InstructionOverrides["__single__"].ShouldBe(Tuned);
        result.Score.ShouldBe(1.0);
        model.Calls.Select(Kit.SystemText).ShouldContain(s => s.Contains(Tuned, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MiproV2_tuning_a_bare_predict_changes_its_system_prompt()
    {
        var model = YesWhenTuned();
        var examples = Examples(4);
        var optimizer = new MiproV2<Predict<KitSig>>(
            Kit.RunAsync, new TextModel("a summary", Tuned),
            candidatesPerSubmodule: 1, numTrials: 6, numDemoSets: 1, minibatchSize: 4, fullEvalEvery: 2, seed: 0);

        // The demo bootstrap runs the program as given, so a bare predict needs an ambient context to run in.
        using var scope = AmpelumContext.Push(Kit.Context(model));
        var result = await optimizer.CompileAsync(
            Predict<KitSig>.FromAmbient(), examples, new AnswerMatch(), Kit.Context(model), ct: Ct);

        result.InstructionOverrides["__single__"].ShouldBe(Tuned);
        result.Score.ShouldBe(1.0);
        model.Calls.Select(Kit.SystemText).ShouldContain(s => s.Contains(Tuned, StringComparison.Ordinal));
    }

    // ---- ChatClients -----------------------------------------------------------------------

    [Fact]
    public void ChatClients_are_empty_by_default()
    {
        new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()), NoDemos, NoOverrides)
            .ChatClients.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_composed_program_sends_a_submodule_to_its_tuned_client_and_the_others_to_the_base_client()
    {
        var baseModel = new ScriptedModel(_ => "base");
        var tunedModel = new ScriptedModel(_ => "tuned");
        var program = new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()), NoDemos, NoOverrides)
        {
            ChatClients = new Dictionary<string, Microsoft.Extensions.AI.IChatClient> { ["a"] = tunedModel },
        };

        var bound = program.Bind(Kit.Context(baseModel));
        var a = await bound.A.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);
        var b = await bound.B.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        a.Value.Answer.ShouldBe("tuned");
        b.Value.Answer.ShouldBe("base");
        tunedModel.Calls.Count.ShouldBe(1);
        baseModel.Calls.Count.ShouldBe(1);
    }

    // ---- a field the program does not set keeps the module's bound value ---------------------------------

    static IReadOnlyDictionary<string, Microsoft.Extensions.AI.IChatClient> Clients(
        params (string Name, Microsoft.Extensions.AI.IChatClient Client)[] clients) =>
        clients.ToDictionary(c => c.Name, c => c.Client);

    [Fact]
    public async Task Binding_again_keeps_what_the_new_program_does_not_set_and_replaces_what_it_sets()
    {
        var baseModel = new ScriptedModel(_ => "base");
        var tunedModel = new ScriptedModel(_ => "tuned");
        var first = new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()),
            new Dictionary<string, IReadOnlyList<Example>> { ["b"] = [Kit.Qa("demo-q", "demo-a")] },
            new Dictionary<string, string> { ["a"] = "FIRST-A", ["b"] = "FIRST-B" })
        {
            ChatClients = Clients(("a", tunedModel)),
        };
        var once = first.Bind(Kit.Context(new ScriptedModel(_ => "unused")));

        // The second program sets only an instruction for b.
        var second = new OptimizedProgram<Pair>(once, NoDemos, new Dictionary<string, string> { ["b"] = "SECOND-B" });
        var twice = second.Bind(Kit.Context(baseModel));
        await twice.A.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);
        await twice.B.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        // a: tuned client and instruction kept.
        var aCall = tunedModel.Calls.Single();
        Kit.SystemText(aCall).ShouldContain("FIRST-A");
        // b: instruction replaced, demo kept, and the client of the new context (b has no tuned client).
        var bCall = baseModel.Calls.Single();
        Kit.SystemText(bCall).ShouldContain("SECOND-B");
        Kit.SystemText(bCall).ShouldNotContain("FIRST-B");
        bCall.Any(m => m.Text.Contains("demo-q")).ShouldBeTrue();
    }

    [Fact]
    public async Task Binding_a_single_module_again_keeps_its_client_demos_and_instruction()
    {
        var baseModel = new ScriptedModel(_ => "base");
        var tunedModel = new ScriptedModel(_ => "tuned");
        var once = new OptimizedProgram<Predict<KitSig>>(
            Predict<KitSig>.FromAmbient(),
            new Dictionary<string, IReadOnlyList<Example>> { ["Predict"] = [Kit.Qa("demo-q", "demo-a")] },
            new Dictionary<string, string> { ["Predict"] = "FIRST" })
        {
            ChatClients = Clients(("Predict", tunedModel)),
        }.Bind(Kit.Context(baseModel));

        var kept = new OptimizedProgram<Predict<KitSig>>(once, NoDemos, NoOverrides).Bind(Kit.Context(baseModel));
        var replaced = new OptimizedProgram<Predict<KitSig>>(
            once, NoDemos, new Dictionary<string, string> { ["Predict"] = "SECOND" }).Bind(Kit.Context(baseModel));
        await kept.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);
        await replaced.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        baseModel.Calls.ShouldBeEmpty();
        var keptCall = tunedModel.Calls[0];
        Kit.SystemText(keptCall).ShouldContain("FIRST");
        keptCall.Any(m => m.Text.Contains("demo-q")).ShouldBeTrue();
        var replacedCall = tunedModel.Calls[1];
        Kit.SystemText(replacedCall).ShouldContain("SECOND");
        Kit.SystemText(replacedCall).ShouldNotContain("FIRST");
    }

    [Fact]
    public async Task A_client_that_is_not_tuned_is_replaced_by_the_client_of_the_context_on_both_paths()
    {
        var firstModel = new ScriptedModel(_ => "first");
        var secondModel = new ScriptedModel(_ => "second");
        var single = new OptimizedProgram<Predict<KitSig>>(Predict<KitSig>.FromAmbient(), NoDemos, NoOverrides)
            .Bind(Kit.Context(firstModel));
        var pair = new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()), NoDemos, NoOverrides)
            .Bind(Kit.Context(firstModel));

        var singleAgain = new OptimizedProgram<Predict<KitSig>>(single, NoDemos, NoOverrides).Bind(Kit.Context(secondModel));
        var pairAgain = new OptimizedProgram<Pair>(pair, NoDemos, NoOverrides).Bind(Kit.Context(secondModel));

        (await singleAgain.ForwardAsync(new KitSig { Question = "q" }, ct: Ct)).Value.Answer.ShouldBe("second");
        (await pairAgain.A.ForwardAsync(new KitSig { Question = "q" }, ct: Ct)).Value.Answer.ShouldBe("second");
        (await pairAgain.B.ForwardAsync(new KitSig { Question = "q" }, ct: Ct)).Value.Answer.ShouldBe("second");
        firstModel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_client_that_is_both_a_provider_result_and_a_base_client_is_replaced_when_user_code_rebinds()
    {
        var shared = new ScriptedModel(_ => "shared");
        var other = new ScriptedModel(_ => "other");
        var tuned = new OptimizedProgram<Pair>(
            new Pair(Predict<KitSig>.FromAmbient(), Predict<KitSig>.FromAmbient()), NoDemos, NoOverrides)
        {
            ChatClients = Clients(("a", shared)),
        }.Bind(Kit.Context(other));

        // User code binds the same instance as the plain client of a new context: no mark, nothing is kept.
        var userBound = new Pair(tuned.A.WithContext(Kit.Context(shared)), tuned.B);
        var again = new OptimizedProgram<Pair>(userBound, NoDemos, NoOverrides).Bind(Kit.Context(other));

        (await again.A.ForwardAsync(new KitSig { Question = "q" }, ct: Ct)).Value.Answer.ShouldBe("other");
        shared.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Demos_and_overrides_set_by_user_code_are_not_kept_but_those_set_by_Bind_are()
    {
        var model = new ScriptedModel(_ => "yes");
        var userContext = Kit.Context(model) with
        {
            DefaultDemos = [Kit.Qa("user-demo-q", "a")],
            InstructionOverrides = new Dictionary<string, string> { ["Predict"] = "USER-INSTRUCTION" },
        };
        var userBound = Predict<KitSig>.FromAmbient().WithContext(userContext);

        var rebound = new OptimizedProgram<Predict<KitSig>>(userBound, NoDemos, NoOverrides).Bind(Kit.Context(model));
        await rebound.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        var call = model.Calls.Single();
        call.Any(m => m.Text.Contains("user-demo-q")).ShouldBeFalse();
        Kit.SystemText(call).ShouldNotContain("USER-INSTRUCTION");

        // The same fields set by Bind survive the next Bind.
        var optimizerBound = new OptimizedProgram<Predict<KitSig>>(
            Predict<KitSig>.FromAmbient(),
            new Dictionary<string, IReadOnlyList<Example>> { ["Predict"] = [Kit.Qa("opt-demo-q", "a")] },
            new Dictionary<string, string> { ["Predict"] = "OPT-INSTRUCTION" }).Bind(Kit.Context(model));
        var kept = new OptimizedProgram<Predict<KitSig>>(optimizerBound, NoDemos, NoOverrides).Bind(Kit.Context(model));
        await kept.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        var keptCall = model.Calls[1];
        keptCall.Any(m => m.Text.Contains("opt-demo-q")).ShouldBeTrue();
        Kit.SystemText(keptCall).ShouldContain("OPT-INSTRUCTION");
    }

    // A submodule that is neither Predict nor ChainOfThought: it cannot tell Bind what it is bound to.
    sealed class Nested(AmpelumContext? context = null)
    {
        public AmpelumContext? Context => context;
        public Nested WithContext(AmpelumContext c) => new(c);
    }

    sealed class Outer(Nested inner) : IComposedModule
    {
        public Nested Inner => inner;

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["inner"] = inner };

        public object Rebind(IReadOnlyDictionary<string, object> s) => new Outer((Nested)s["inner"]);
    }

    [Fact]
    public void A_submodule_that_is_not_a_Predict_or_ChainOfThought_gets_no_carry_over()
    {
        var first = new ScriptedModel(_ => "first");
        var second = new ScriptedModel(_ => "second");
        var once = new OptimizedProgram<Outer>(
            new Outer(new Nested()),
            new Dictionary<string, IReadOnlyList<Example>> { ["inner"] = [Kit.Qa("d", "a")] },
            new Dictionary<string, string> { ["inner"] = "INSTRUCTION" })
        {
            ChatClients = Clients(("inner", first)),
        }.Bind(Kit.Context(second));
        once.Inner.Context!.Chat.ShouldBeSameAs(first);

        var again = new OptimizedProgram<Outer>(once, NoDemos, NoOverrides).Bind(Kit.Context(second));

        again.Inner.Context!.DefaultDemos.ShouldBeNull();
        again.Inner.Context.InstructionOverrides.ShouldBeNull();
        again.Inner.Context.Chat.ShouldBeSameAs(second);
    }

    // ---- a single module with several keys ------------------------------------------------------------------

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task With_several_override_keys_the_module_keeps_its_own_name(string moduleName)
    {
        var model = new ScriptedModel(_ => "yes");
        var program = new OptimizedProgram<Predict<KitSig>>(
            Predict<KitSig>.FromAmbient(), NoDemos,
            new Dictionary<string, string> { ["first"] = "FIRST-INSTRUCTION", ["second"] = "SECOND-INSTRUCTION" });

        var bound = program.Bind(Kit.Context(model) with { SubmoduleName = moduleName });
        await bound.ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        var system = Kit.SystemText(model.Calls.Single());
        system.ShouldContain(moduleName == "first" ? "FIRST-INSTRUCTION" : "SECOND-INSTRUCTION");
        system.ShouldNotContain(moduleName == "first" ? "SECOND-INSTRUCTION" : "FIRST-INSTRUCTION");
    }

    [Fact]
    public async Task With_several_chat_clients_the_single_module_keeps_its_own_client()
    {
        var baseModel = new ScriptedModel(_ => "base");
        var x = new ScriptedModel(_ => "x");
        var y = new ScriptedModel(_ => "y");
        var program = new OptimizedProgram<Predict<KitSig>>(Predict<KitSig>.FromAmbient(), NoDemos, NoOverrides)
        {
            ChatClients = Clients(("x", x), ("y", y)),
        };

        var prediction = await program.Bind(Kit.Context(baseModel))
            .ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        prediction.Value.Answer.ShouldBe("base");
        x.Calls.ShouldBeEmpty();
        y.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_single_module_program_sends_its_calls_to_the_tuned_client()
    {
        var baseModel = new ScriptedModel(_ => "base");
        var tunedModel = new ScriptedModel(_ => "tuned");
        var program = new OptimizedProgram<Predict<KitSig>>(Predict<KitSig>.FromAmbient(), NoDemos, NoOverrides)
        {
            ChatClients = new Dictionary<string, Microsoft.Extensions.AI.IChatClient> { ["Predict"] = tunedModel },
        };

        var prediction = await program.Bind(Kit.Context(baseModel))
            .ForwardAsync(new KitSig { Question = "q" }, ct: Ct);

        prediction.Value.Answer.ShouldBe("tuned");
        baseModel.Calls.ShouldBeEmpty();
    }
}
