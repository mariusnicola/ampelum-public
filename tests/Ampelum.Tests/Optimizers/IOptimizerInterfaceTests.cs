using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public class IOptimizerInterfaceTests
{
    // ─── IMetric ───────────────────────────────────────────────────────────────

    [Fact]
    public void IMetric_LambdaImpl_ReturnsScore()
    {
        IMetric metric = new LambdaMetric((actual, expected, _) => 1.0);
        var sig = SignatureMetadataCache.For<MinimalSig>();
        metric.Score(new object(), new object(), sig).ShouldBe(1.0);
    }

    // ─── OptimizerProgress ─────────────────────────────────────────────────────

    [Fact]
    public void OptimizerProgress_DefaultExtras_IsEmpty()
    {
        var p = new OptimizerProgress("Train", 1, 10, 0.5);
        p.Extras.ShouldBeEmpty();
    }

    [Fact]
    public void OptimizerProgress_PositionalProperties_Readable()
    {
        var p = new OptimizerProgress("Train", 1, 10, 0.5);
        p.Phase.ShouldBe("Train");
        p.Step.ShouldBe(1);
        p.TotalSteps.ShouldBe(10);
        p.BestScore.ShouldBe(0.5);
    }

    [Fact]
    public void OptimizerProgress_WithExtras_Populated()
    {
        var extras = new Dictionary<string, object?> { ["foo"] = 42 };
        var p = new OptimizerProgress("Eval", 3, 10, 0.8, extras);
        p.Extras["foo"].ShouldBe(42);
    }

    // ─── OptimizerException ────────────────────────────────────────────────────

    [Fact]
    public void OptimizerException_IsAmpelumException()
    {
        var ex = new OptimizerException("boom");
        ex.ShouldBeAssignableTo<AmpelumException>();
        ex.Message.ShouldBe("boom");
    }

    [Fact]
    public void OptimizerException_WithInner_Chains()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new OptimizerException("outer", inner);
        ex.InnerException.ShouldBeSameAs(inner);
    }

    // ─── IOptimizer<TProgram> — contract via stub ──────────────────────────────

    [Fact]
    public async Task IOptimizer_CompileAsync_ReturnsOptimizedProgram()
    {
        IOptimizer<FakeProgram> opt = new AlwaysSuccessOptimizer();
        var trainset = new[] { new Example(new Dictionary<string, object?> { ["q"] = "hi" },
                                           new Dictionary<string, object?> { ["a"] = "there" }) };
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var ctx = TestHelpers.MakeContext();
        var result = await opt.CompileAsync(new FakeProgram(), trainset, metric, ctx, ct: TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result.Program.ShouldBeOfType<FakeProgram>();
    }

    // ─── helpers / stubs ───────────────────────────────────────────────────────

    [Signature("Minimal.")]
    public sealed record MinimalSig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class FakeProgram { }

    sealed class LambdaMetric(Func<object, object, SignatureMetadata, double> fn) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata sig) => fn(actual, expected, sig);
    }

    sealed class AlwaysSuccessOptimizer : IOptimizer<FakeProgram>
    {
        public Task<OptimizedProgram<FakeProgram>> CompileAsync(
            FakeProgram program,
            IReadOnlyList<Example> trainset,
            IMetric metric,
            AmpelumContext context,
            IProgress<OptimizerProgress>? progress = null,
            CancellationToken ct = default)
            => Task.FromResult(new OptimizedProgram<FakeProgram>(program, new Dictionary<string, IReadOnlyList<Example>>(), new Dictionary<string, string>()));
    }
}
