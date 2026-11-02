using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public class OptimizedProgramTests
{
    [Fact]
    public void OptimizedProgram_HoldsProgram()
    {
        var prog = new FakeProgram();
        var op = MakeOp(prog);
        op.Program.ShouldBeSameAs(prog);
    }

    [Fact]
    public void OptimizedProgram_DemosBySubmodule_Readable()
    {
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["qa"] = [MakeExample()]
        };
        var op = new OptimizedProgram<FakeProgram>(new FakeProgram(), demos, new Dictionary<string, string>());
        op.DemosBySubmodule["qa"].Count.ShouldBe(1);
    }

    [Fact]
    public void OptimizedProgram_InstructionOverrides_Readable()
    {
        var overrides = new Dictionary<string, string> { ["qa"] = "Be concise." };
        var op = new OptimizedProgram<FakeProgram>(new FakeProgram(), new Dictionary<string, IReadOnlyList<Example>>(), overrides);
        op.InstructionOverrides["qa"].ShouldBe("Be concise.");
    }

    [Fact]
    public void OptimizedProgram_Score_DefaultsToNull()
    {
        var op = MakeOp(new FakeProgram());
        op.Score.ShouldBeNull();
    }

    [Fact]
    public void OptimizedProgram_Score_CanBeSet()
    {
        var op = MakeOp(new FakeProgram()) with { Score = 0.95 };
        op.Score.ShouldBe(0.95);
    }

    [Fact]
    public void OptimizedProgram_TrainsetSize_DefaultsToZero()
    {
        var op = MakeOp(new FakeProgram());
        op.TrainsetSize.ShouldBe(0);
    }

    [Fact]
    public void OptimizedProgram_TrainsetSize_CanBeSet()
    {
        var op = MakeOp(new FakeProgram()) with { TrainsetSize = 50 };
        op.TrainsetSize.ShouldBe(50);
    }

    // ─── Path A tests (IComposedModule) ────────────────────────────────────────

    [Fact]
    public void Bind_PathA_ReturnsDifferentInstance()
    {
        var (prog, op) = MakeComposedOp();
        var ctx = TestHelpers.MakeContext();
        var bound = op.Bind(ctx);
        bound.ShouldNotBeSameAs(prog);
    }

    [Fact]
    public void Bind_PathA_BoundSubmoduleHasSubmoduleName()
    {
        var (_, op) = MakeComposedOp(
            demosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>
            {
                ["qa"] = [MakeExample()]
            });
        var ctx = TestHelpers.MakeContext();
        var bound = (ComposedProg)op.Bind(ctx);
        bound.QaContext!.SubmoduleName.ShouldBe("qa");
    }

    [Fact]
    public void Bind_PathA_BoundSubmoduleHasDemos()
    {
        var demo = MakeExample();
        var (_, op) = MakeComposedOp(
            demosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>
            {
                ["qa"] = [demo]
            });
        var ctx = TestHelpers.MakeContext();
        var bound = (ComposedProg)op.Bind(ctx);
        bound.QaContext!.DefaultDemos.ShouldNotBeNull();
        bound.QaContext!.DefaultDemos!.Count.ShouldBe(1);
    }

    [Fact]
    public void Bind_PathA_BoundSubmoduleHasInstructionOverride()
    {
        var (_, op) = MakeComposedOp(
            instructionOverrides: new Dictionary<string, string>
            {
                ["qa"] = "Be very concise."
            });
        var ctx = TestHelpers.MakeContext();
        var bound = (ComposedProg)op.Bind(ctx);
        bound.QaContext!.InstructionOverrides.ShouldNotBeNull();
        bound.QaContext!.InstructionOverrides!["qa"].ShouldBe("Be very concise.");
    }

    [Fact]
    public void Bind_PathA_UnknownSubmoduleGetsBaseContext()
    {
        var (_, op) = MakeComposedOp();
        var ctx = TestHelpers.MakeContext();
        var bound = (ComposedProg)op.Bind(ctx);
        bound.QaContext!.DefaultDemos.ShouldBeNull();
        bound.QaContext!.InstructionOverrides.ShouldBeNull();
    }

    [Fact]
    public void Bind_PathA_DoesNotMutateOriginalProgram()
    {
        var (prog, op) = MakeComposedOp(
            demosBySubmodule: new Dictionary<string, IReadOnlyList<Example>>
            {
                ["qa"] = [MakeExample()]
            });
        var ctx = TestHelpers.MakeContext();
        _ = op.Bind(ctx);
        prog.QaContext.ShouldBeNull();
    }

    [Fact]
    public void Bind_NullBaseContext_Throws()
    {
        var (_, op) = MakeComposedOp();
        Should.Throw<ArgumentNullException>(() => op.Bind(null!));
    }

    [Fact]
    public void Bind_PathA_ModuleWithoutWithContext_ThrowsOptimizerException()
    {
        var prog = new BadComposedProg();
        var op = new OptimizedProgram<BadComposedProg>(
            prog,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var ctx = TestHelpers.MakeContext();
        Should.Throw<OptimizerException>(() => op.Bind(ctx));
    }

    // ─── Path B tests (single-module fallback) ─────────────────────────────────

    [Fact]
    public void Bind_PathB_SingleModuleProgram_ReturnsBoundInstance()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig2>(ctx);
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["__single__"] = [MakeExample()]
        };
        var op = new OptimizedProgram<Predict<MinimalSig2>>(predict, demos, new Dictionary<string, string>());
        var bound = op.Bind(ctx);
        bound.ShouldNotBeSameAs(predict);
    }

    [Fact]
    public void Bind_PathB_NoDemos_NoOverrides_ReturnsBoundInstance()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig2>(ctx);
        var op = new OptimizedProgram<Predict<MinimalSig2>>(
            predict,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var bound = op.Bind(ctx);
        bound.ShouldNotBeNull();
    }

    [Fact]
    public void Bind_PathB_EmptyDemoListEntry_DemosNull()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig2>(ctx);
        var demos = new Dictionary<string, IReadOnlyList<Example>>
        {
            ["__single__"] = Array.Empty<Example>()
        };
        var op = new OptimizedProgram<Predict<MinimalSig2>>(predict, demos, new Dictionary<string, string>());
        var bound = op.Bind(ctx);
        bound.ShouldNotBeNull();
    }

    [Fact]
    public void Bind_PathB_WithInstructionOverrides_ReturnsBoundInstance()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig2>(ctx);
        var op = new OptimizedProgram<Predict<MinimalSig2>>(
            predict,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string> { ["__single__"] = "Be precise." });
        var bound = op.Bind(ctx);
        bound.ShouldNotBeNull();
    }

    [Fact]
    public void Bind_PathB_ThrowsOptimizerException_WhenNoWithContext()
    {
        var ctx = TestHelpers.MakeContext();
        var op = new OptimizedProgram<NoContextProg>(
            new NoContextProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        Should.Throw<OptimizerException>(() => op.Bind(ctx));
    }

    sealed class NoContextProg { /* no WithContext method */ }

    /// <summary>
    /// Regression: a module exposing only an `internal WithContext(AmpelumContext)` must bind
    /// successfully — the reflection lookup needs Public|NonPublic|Instance flags.
    /// </summary>
    [Fact]
    public void Bind_PathB_InternalWithContext_ReturnsBoundInstance()
    {
        var ctx = TestHelpers.MakeContext();
        var op = new OptimizedProgram<InternalWithContextProg>(
            new InternalWithContextProg(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var bound = op.Bind(ctx);
        bound.ShouldNotBeNull();
        bound.ShouldBeOfType<InternalWithContextProg>();
    }

    sealed class InternalWithContextProg
    {
#pragma warning disable CA1822 // must be instance for reflection lookup
        internal InternalWithContextProg WithContext(AmpelumContext ctx) => new();
#pragma warning restore CA1822
    }

    [Fact]
    public void Bind_PathA_ModuleWithContextReturnsNull_ThrowsOptimizerException()
    {
        var prog = new NullReturningProg();
        var op = new OptimizedProgram<NullReturningProg>(
            prog,
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var ctx = TestHelpers.MakeContext();
        Should.Throw<OptimizerException>(() => op.Bind(ctx));
    }

    sealed class FakeProgram { }

    [Signature("Q-and-A.")]
    public sealed record MinimalSig2 : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    /// <summary>Exposes the AmpelumContext that was injected into its qa submodule, for assertions.</summary>
    sealed class ComposedProg : IComposedModule
    {
        public Predict<MinimalSig2> Qa { get; private set; } = new(TestHelpers.MakeContext());
        public AmpelumContext? QaContext { get; private set; }

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["qa"] = Qa };

        public object Rebind(IReadOnlyDictionary<string, object> newSubmodules)
        {
            var copy = new ComposedProg();
            copy.Qa = (Predict<MinimalSig2>)newSubmodules["qa"];
            var ctxField = typeof(Predict<MinimalSig2>)
                .GetField("_ctorCtx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            copy.QaContext = (AmpelumContext?)ctxField.GetValue(copy.Qa);
            return copy;
        }
    }

    /// <summary>Module that does not expose WithContext(AmpelumContext).</summary>
    sealed class BadModule { }

    sealed class BadComposedProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["bad"] = new BadModule() };

        public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) => this;
    }

    /// <summary>Module whose WithContext returns null.</summary>
    sealed class NullReturningModule
    {
#pragma warning disable CA1822 // mark as static — must be instance for reflection signature
        public object? WithContext(AmpelumContext ctx) => null;
#pragma warning restore CA1822
    }

    sealed class NullReturningProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["x"] = new NullReturningModule() };

        public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) => this;
    }

    static (ComposedProg, OptimizedProgram<ComposedProg>) MakeComposedOp(
        IReadOnlyDictionary<string, IReadOnlyList<Example>>? demosBySubmodule = null,
        IReadOnlyDictionary<string, string>? instructionOverrides = null)
    {
        var prog = new ComposedProg();
        var op = new OptimizedProgram<ComposedProg>(
            prog,
            demosBySubmodule ?? new Dictionary<string, IReadOnlyList<Example>>(),
            instructionOverrides ?? new Dictionary<string, string>());
        return (prog, op);
    }

    static OptimizedProgram<FakeProgram> MakeOp(FakeProgram p) =>
        new(p, new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>());

    static Example MakeExample() =>
        new(new Dictionary<string, object?> { ["q"] = "hi" },
            new Dictionary<string, object?> { ["a"] = "there" });
}
