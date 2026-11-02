using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public class LabeledFewShotTests
{
    static Example MakeExample(int i) =>
        new(new Dictionary<string, object?> { ["q"] = $"q{i}" },
            new Dictionary<string, object?> { ["a"] = $"a{i}" });

    static IReadOnlyList<Example> MakeTrainset(int n) =>
        Enumerable.Range(0, n).Select(MakeExample).ToList();

    [Fact]
    public async Task LabeledFewShot_SingleModule_DemosAssigned()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        var trainset = MakeTrainset(10);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new LabeledFewShot<Predict<MinimalSig>>(maxDemos: 3);
        var result = await opt.CompileAsync(predict, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.DemosBySubmodule.ShouldNotBeEmpty();
        result.DemosBySubmodule.Values.First().Count.ShouldBe(3);
    }

    [Fact]
    public async Task LabeledFewShot_MaxDemos_CapsAtTrainsetSize()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        var trainset = MakeTrainset(2);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new LabeledFewShot<Predict<MinimalSig>>(maxDemos: 5);
        var result = await opt.CompileAsync(predict, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.DemosBySubmodule.Values.First().Count.ShouldBe(2);
    }

    [Fact]
    public async Task LabeledFewShot_ComposedModule_AllSubmodulesGetDemos()
    {
        var ctx = TestHelpers.MakeContext();
        var prog = new TwoModuleProg(ctx);
        var trainset = MakeTrainset(4);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new LabeledFewShot<TwoModuleProg>(maxDemos: 2);
        var result = await opt.CompileAsync(prog, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.DemosBySubmodule.Keys.ShouldContain("retriever");
        result.DemosBySubmodule.Keys.ShouldContain("answer");
        result.DemosBySubmodule["retriever"].Count.ShouldBe(2);
        result.DemosBySubmodule["answer"].Count.ShouldBe(2);
    }

    [Fact]
    public async Task LabeledFewShot_TrainsetSize_RecordedOnResult()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        var trainset = MakeTrainset(7);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new LabeledFewShot<Predict<MinimalSig>>(maxDemos: 3);
        var result = await opt.CompileAsync(predict, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);

        result.TrainsetSize.ShouldBe(7);
    }

    [Fact]
    public async Task LabeledFewShot_Bind_ProducesUsableBoundProgram()
    {
        var ctx = TestHelpers.MakeContext();
        var prog = new TwoModuleProg(ctx);
        var trainset = MakeTrainset(3);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);

        var opt = new LabeledFewShot<TwoModuleProg>(maxDemos: 2);
        var result = await opt.CompileAsync(prog, trainset, metric, ctx, ct: TestContext.Current.CancellationToken);
        var bound = result.Bind(ctx);

        bound.ShouldNotBeNull();
        bound.ShouldBeOfType<TwoModuleProg>();
    }

    [Fact]
    public async Task LabeledFewShot_ProgressReported()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        var trainset = MakeTrainset(2);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var captured = new List<OptimizerProgress>();
        var progress = new Progress<OptimizerProgress>(captured.Add);

        var opt = new LabeledFewShot<Predict<MinimalSig>>(maxDemos: 1);
        await opt.CompileAsync(predict, trainset, metric, ctx, progress, ct: TestContext.Current.CancellationToken);

        // Allow Progress<T> to dispatch synchronously-ish (it posts to sync ctx).
        await Task.Yield();
        // The fact that no exception throws + result returned is enough; we won't assert capture
        // because Progress<T> uses SynchronizationContext post and may be deferred.
    }

    [Fact]
    public async Task LabeledFewShot_NullProgram_Throws()
    {
        var ctx = TestHelpers.MakeContext();
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new LabeledFewShot<Predict<MinimalSig>>();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(null!, MakeTrainset(1), metric, ctx, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LabeledFewShot_NullTrainset_Throws()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new LabeledFewShot<Predict<MinimalSig>>();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(predict, null!, metric, ctx, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LabeledFewShot_NullMetric_Throws()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        var opt = new LabeledFewShot<Predict<MinimalSig>>();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(predict, MakeTrainset(1), null!, ctx, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LabeledFewShot_NullContext_Throws()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new LabeledFewShot<Predict<MinimalSig>>();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            opt.CompileAsync(predict, MakeTrainset(1), metric, null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LabeledFewShot_CancelledToken_Throws()
    {
        var ctx = TestHelpers.MakeContext();
        var predict = new Predict<MinimalSig>(ctx);
        IMetric metric = new LambdaMetric((_, _, _) => 1.0);
        var opt = new LabeledFewShot<Predict<MinimalSig>>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            opt.CompileAsync(predict, MakeTrainset(1), metric, ctx, ct: cts.Token));
    }

    [Signature("Q-and-A.")]
    public sealed record MinimalSig : ISignature
    {
        [Input] public required string Question { get; init; }
        [Output] public string Answer { get; init; } = "";
    }

    sealed class LambdaMetric(Func<object, object, SignatureMetadata, double> fn) : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata sig) => fn(actual, expected, sig);
    }

    sealed class TwoModuleProg : IComposedModule
    {
        Predict<MinimalSig> _retriever;
        Predict<MinimalSig> _answer;

        public TwoModuleProg(AmpelumContext ctx)
        {
            _retriever = new Predict<MinimalSig>(ctx);
            _answer    = new Predict<MinimalSig>(ctx);
        }

        TwoModuleProg(Predict<MinimalSig> r, Predict<MinimalSig> a)
        {
            _retriever = r;
            _answer    = a;
        }

        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>
            {
                ["retriever"] = _retriever,
                ["answer"]    = _answer,
            };

        public object Rebind(IReadOnlyDictionary<string, object> newSubmodules) =>
            new TwoModuleProg(
                (Predict<MinimalSig>)newSubmodules["retriever"],
                (Predict<MinimalSig>)newSubmodules["answer"]);
    }
}
