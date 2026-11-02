using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum;
using Shouldly;
using Xunit;

namespace Ampelum.Tests.Optimizers;

public sealed class BootstrapFewShotWithRandomSearchTests
{
    [Fact]
    public void Ctor_NullProgramRunner_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new BootstrapFewShotWithRandomSearch<FakeProg>(null!));

    [Fact]
    public void Ctor_NumCandidatesZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new BootstrapFewShotWithRandomSearch<FakeProg>(
                NoopRunner, numCandidates: 0));

    [Fact]
    public void Ctor_MaxBootstrappedDemosNegative_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new BootstrapFewShotWithRandomSearch<FakeProg>(
                NoopRunner, maxBootstrappedDemos: -1));

    [Fact]
    public async Task CompileAsync_NullProgram_Throws()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(NoopRunner);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(null!, MakeTrainset(1), new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullTrainset_Throws()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(NoopRunner);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), null!, new TrueMetric(),
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(NoopRunner);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), null!,
                TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_NullContext_Throws()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(NoopRunner);
        await Should.ThrowAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1), new TrueMetric(),
                null!, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompileAsync_ReturnsResult()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner, numCandidates: 2, maxBootstrappedDemos: 1, seed: 42);

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(2), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompileAsync_NoSeed_StillRuns()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner, numCandidates: 2, maxBootstrappedDemos: 1, seed: null);

        var result = await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(2), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompileAsync_ReportsProgress()
    {
        var reports = new List<OptimizerProgress>();
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner, numCandidates: 3, seed: 1);

        await optimizer.CompileAsync(
            new FakeProg(), MakeTrainset(1), new TrueMetric(),
            TestHelpers.MakeContext(),
            new SyncProgress<OptimizerProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        reports.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CompileAsync_CancelledToken_Throws()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(NoopRunner);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(new FakeProg(), MakeTrainset(1),
                new TrueMetric(), TestHelpers.MakeContext(), null, cts.Token));
    }

    [Fact]
    public async Task CompileAsync_EmptyTrainset_Returns()
    {
        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            NoopRunner, numCandidates: 1, seed: 0);

        var result = await optimizer.CompileAsync(
            new FakeProg(), Array.Empty<Example>(), new TrueMetric(),
            TestHelpers.MakeContext(), null, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    static Task NoopRunner(FakeProg p, Example e, AmpelumContext ctx, CancellationToken ct) =>
        Task.CompletedTask;

    static List<Example> MakeTrainset(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Example(
                new Dictionary<string, object?> { ["q"] = $"q{i}" },
                new Dictionary<string, object?> { ["a"] = $"a{i}" }))
            .ToList();

    sealed class SyncProgress<T>(Action<T> cb) : IProgress<T>
    { public void Report(T value) => cb(value); }

    sealed class TrueMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature) => 1.0;
    }

    sealed class FakeProg : IComposedModule
    {
        public IReadOnlyDictionary<string, object> Submodules =>
            new Dictionary<string, object>();
        public object Rebind(IReadOnlyDictionary<string, object> r) => this;
    }

    [Fact]
    public async Task CompileAsync_WithScoringRunner_RunsRunnerAndUsesItsScore()
    {
        // Regression: when a scoringRunner is supplied, BootstrapFewShotWithRandomSearch must
        // delegate to it (rather than scoring ex.Outputs against itself).
        int runnerCalls = 0;
        Task ProgRunner(FakeProg _, Example ex, AmpelumContext runCtx, CancellationToken __)
        {
            runCtx.TraceSink.Write(new TraceRecord(
                Id: Guid.NewGuid(),
                ModuleName: "qa",
                SignatureType: typeof(string),
                Inputs: ex.Inputs,
                Outputs: ex.Outputs,
                PromptMessages: [],
                RawResponse: "",
                Usage: null,
                Duration: TimeSpan.Zero,
                Error: null,
                Metadata: new Dictionary<string, object?>()));
            return Task.CompletedTask;
        }
        Task<Example> ScoringRunner(FakeProg _, Example ex, AmpelumContext __, CancellationToken ___)
        {
            runnerCalls++;
            return Task.FromResult(new Example(ex.Inputs,
                new Dictionary<string, object?> { ["a"] = "WRONG" }));
        }

        var trainset = new List<Example>
        {
            new(new Dictionary<string, object?> { ["q"] = "x" },
                new Dictionary<string, object?> { ["a"] = "alpha" }),
        };

        IMetric metric = new ExactMatchMetric();

        var optimizer = new BootstrapFewShotWithRandomSearch<FakeProg>(
            programRunner: ProgRunner,
            numCandidates: 2,
            maxBootstrappedDemos: 1,
            seed: 0,
            scoringRunner: ScoringRunner);

        var ctx = TestHelpers.MakeContext();
        var result = await optimizer.CompileAsync(
            new FakeProg(), trainset, metric, ctx, null,
            TestContext.Current.CancellationToken);

        // ScoringRunner ran (degenerate mode would never have called it).
        runnerCalls.ShouldBeGreaterThanOrEqualTo(1);
        // Runner returns "WRONG" so score is 0; degenerate mode would have returned 1.0.
        result.Score.ShouldBe(0.0);
    }

    sealed class ExactMatchMetric : IMetric
    {
        public double Score(object actual, object expected, SignatureMetadata signature)
        {
            var a = (IReadOnlyDictionary<string, object?>)actual;
            var e = (IReadOnlyDictionary<string, object?>)expected;
            return a.TryGetValue("a", out var av)
                && e.TryGetValue("a", out var ev)
                && Equals(av, ev) ? 1.0 : 0.0;
        }
    }
}
