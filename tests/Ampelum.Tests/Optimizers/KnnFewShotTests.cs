using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Retrieval;
using SignalGrid.Ampelum;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class KnnFewShotTests
{
    [Fact]
    public void Ctor_NullEmbeddingGenerator_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new KnnFewShot<FakeProg>(null!, k: 2));

    [Fact]
    public void Ctor_KZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 0));

    [Fact]
    public async Task CompileAsync_NullProgram_Throws()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(null!, Array.Empty<Example>(),
                new TrueMetric(), TestHelpers.MakeContext(),
                null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullTrainset_Throws()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), null!,
                new TrueMetric(), TestHelpers.MakeContext(),
                null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), Array.Empty<Example>(),
                null!, TestHelpers.MakeContext(),
                null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), Array.Empty<Example>(),
                new TrueMetric(), null!,
                null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_ReturnsKnnBoundProgram()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        var trainset = MakeTrainset(2);

        var result = await optimizer.CompileAsync(
            new FakeProg(), trainset, new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Program.ShouldBeOfType<KnnBoundProgram<FakeProg>>();
        result.Program.K.ShouldBe(1);
    }

    [Fact]
    public async Task CompileAsync_ReportsProgress()
    {
        var reports = new List<OptimizerProgress>();
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);

        await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(2), new TrueMetric(),
            TestHelpers.MakeContext(),
            new SyncProgress<OptimizerProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        reports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_StillReturnsBound()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        var result = await optimizer.CompileAsync(
            new FakeProg(), Array.Empty<Example>(), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.Program.ShouldBeOfType<KnnBoundProgram<FakeProg>>();
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(
                new FakeProg(), MakeTrainset(1),
                new TrueMetric(), TestHelpers.MakeContext(), null, cts.Token));
    }

    [Fact]
    public async Task ResolveNeighboursAsync_ReturnsTopK()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 2);
        var trainset = MakeTrainset(3);

        var compiled = await optimizer.CompileAsync(
            new FakeProg(), trainset, new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        var neighbours = await compiled.Program.ResolveNeighboursAsync(
            "query", TestContext.Current.CancellationToken);

        neighbours.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ResolveNeighboursAsync_NullQuery_Throws()
    {
        var optimizer = new KnnFewShot<FakeProg>(new FakeEmbedder(), k: 1);
        var compiled = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(1), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            compiled.Program.ResolveNeighboursAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void KnnBoundProgram_NonComposedInner_SubmodulesEmpty()
    {
        var config = new KnnConfig(new FakeEmbedder(),
            new InMemoryVectorStore(), 1, []);
        var bound = new KnnBoundProgram<FakeProg>(new FakeProg(), config);
        bound.Submodules.ShouldBeEmpty();
    }

    [Fact]
    public void KnnBoundProgram_NonComposedInner_RebindReturnsSelf()
    {
        var config = new KnnConfig(new FakeEmbedder(),
            new InMemoryVectorStore(), 1, []);
        var bound = new KnnBoundProgram<FakeProg>(new FakeProg(), config);
        bound.Rebind(new Dictionary<string, object>()).ShouldBeSameAs(bound);
    }

    [Fact]
    public void KnnBoundProgram_ComposedInner_DelegatesSubmodules()
    {
        var config = new KnnConfig(new FakeEmbedder(),
            new InMemoryVectorStore(), 1, []);
        var bound = new KnnBoundProgram<ComposedFake>(new ComposedFake(), config);
        bound.Submodules.Keys.ShouldContain("m");
    }

    [Fact]
    public void KnnBoundProgram_ComposedInner_RebindReturnsNewWrapper()
    {
        var config = new KnnConfig(new FakeEmbedder(),
            new InMemoryVectorStore(), 1, []);
        var bound = new KnnBoundProgram<ComposedFake>(new ComposedFake(), config);
        var rebound = bound.Rebind(new Dictionary<string, object> { ["m"] = "new" });
        rebound.ShouldBeOfType<KnnBoundProgram<ComposedFake>>();
        ((KnnBoundProgram<ComposedFake>)rebound).ShouldNotBeSameAs(bound);
    }

    [Fact]
    public void KnnBoundProgram_NullInner_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new KnnBoundProgram<FakeProg>(null!, new KnnConfig(
                new FakeEmbedder(), new InMemoryVectorStore(), 1, [])));

    [Fact]
    public void KnnBoundProgram_NullConfig_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new KnnBoundProgram<FakeProg>(new FakeProg(), null!));

    [Fact]
    public void BuildQueryText_NullValue_HandledAsEmpty()
    {
        var ex = new Example(
            new Dictionary<string, object?> { ["a"] = null, ["b"] = "x" },
            new Dictionary<string, object?>());
        var s = KnnFewShot<FakeProg>.BuildQueryText(ex);
        s.ShouldContain("x");
    }

    static List<Example> MakeTrainset(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    { public void Report(T value) => cb(value); }

    sealed class FakeProg { }

    sealed class ComposedFake : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object> { ["m"] = "module" };
        public object Rebind(IReadOnlyDictionary<string, object> r) => new ComposedFake();
    }

    sealed class TrueMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }

    sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        readonly EmbeddingGeneratorMetadata _meta = new(nameof(FakeEmbedder));
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken ct = default)
        {
            var rng = new Random(42);
            var result = new GeneratedEmbeddings<Embedding<float>>(
                values.Select(v =>
                {
                    var arr = new float[3];
                    for (int i = 0; i < arr.Length; i++) arr[i] = (float)rng.NextDouble();
                    return new Embedding<float>(arr);
                }).ToList());
            return Task.FromResult(result);
        }
        public object? GetService(Type t, object? key = null) =>
            t == typeof(EmbeddingGeneratorMetadata) ? _meta : null;
        public void Dispose() { }
    }
}
